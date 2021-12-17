using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Playwright;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;

const string chessDotComUserName = "twitch_plays_chessdot";
const string twitchUsername = "twitch_plays_chessdotcom";

TimeSpan streamDelay = TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("TWITCH_DELAY") ?? "0"));

Dictionary<char, PieceType> pieceLetterToTypes = new()
{
    ['k'] = PieceType.King,
    ['q'] = PieceType.Queen,
    ['r'] = PieceType.Rook,
    ['b'] = PieceType.Bishop,
    ['n'] = PieceType.Knight,
    ['p'] = PieceType.Pawn,
};

Dictionary<PieceType, char> pieceTypeToLetter = new()
{
    [PieceType.King] = 'K',
    [PieceType.Queen] = 'Q',
    [PieceType.Rook] = 'R',
    [PieceType.Bishop] = 'B',
    [PieceType.Knight] = 'N',
    [PieceType.Pawn] = 'P',
};

if (args.Length == 0)
{
    Console.Error.WriteLine("use:\n- offline [botname]\n- online  [playername]");
    return;
}

TwitchClient twitch = CreateTwitchClient();
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false,
    // Devtools = true,
});
var chessPage = await browser.NewPageAsync(new BrowserNewPageOptions
{
    ScreenSize = new ScreenSize { Width = 1920, Height = 1080 },
    ViewportSize = new ViewportSize { Width = 1600, Height = 900 },
    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/95.0.4638.69 Safari/537.36",
    BaseURL = "https://www.chess.com",
});

await LoginAsync(chessPage);
while (true)
{
    Console.Clear();
    if (args[0] == "offline")
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Bot name required");
            return;
        }
        await StartOfflineGameAsync(chessPage, args[1]);
    }
    else if (args[0] == "online")
    {
        await StartOnlineGameAsync(chessPage, args.Length > 1 ? args[1] : null);
    }
    else
    {
        Console.Error.WriteLine($"Invalid mode '{args[0]}'");
        return;
    }

    await HideHintsAsync(chessPage);
    await RunGameAsync(chessPage, twitch);
    await Task.Delay(TimeSpan.FromSeconds(15));
}

async Task LoginAsync(IPage page)
{
    string chessDotComPassword = Environment.GetEnvironmentVariable("CHESSDOTCOM_PASSWORD")
                         ?? throw new InvalidOperationException("CHESSDOTCOM_PASSWORD not set");
    do
    {
        await page.GotoAsync("login_and_go",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await page.FillAsync("#username", chessDotComUserName);
        await page.FillAsync("#password", chessDotComPassword);
        await page.CheckAsync("#_remember_me");
        await page.ClickAsync("#login");
        if (!page.Url.EndsWith("/home", StringComparison.Ordinal))
        {
            await page.WaitForNavigationAsync();
        }
    } while (!page.Url.EndsWith("/home", StringComparison.Ordinal));

    await CloseTrialModalAsync(page);
    await CloseBottomBannerAsync(page);
}

async Task CloseTrialModalAsync(IPage page)
{
    var modalEl = await page.QuerySelectorAsync(".modal-trial-component");
    if (modalEl == null)
    {
        return;
    }

    var buttonEl = await modalEl.QuerySelectorAsync(".ui_outside-close-component");
    if (buttonEl == null)
    {
        return;
    }

    await buttonEl.ClickAsync();
}


async Task CloseBottomBannerAsync(IPage page)
{
    var modalEl = await page.QuerySelectorAsync(".bottom-banner-component");
    if (modalEl == null)
    {
        return;
    }

    var buttonEl = await modalEl.QuerySelectorAsync(".bottom-banner-close");
    if (buttonEl == null)
    {
        return;
    }

    await buttonEl.ClickAsync();
}

async Task StartOfflineGameAsync(IPage page, string botName)
{
    await page.GotoAsync("play/computer");

    // Select opponent.
    await page.ClickAsync($"[data-bot-name=\"{botName}\"]");
    await page.ClickAsync(".selection-menu-footer > button");

    // Play.
    await page.ClickAsync(".selection-menu-footer > button");
}

async Task StartOnlineGameAsync(IPage page, string? playerName)
{
    await page.GotoAsync("play/online");

    if (playerName != null)
    {
        // Play with friend.
        await page.ClickAsync(".new-game-option-handshake");
        // Input friend nane.
        await page.FillAsync(".play-friend-user-search-user-search input", playerName);
        // Click on autocomplete.
        await page.ClickAsync(".play-friend-user-search-user-search > ul > li");
        // Uncheck rated game.
        await page.UncheckAsync(".new-game-margin-component input[type=checkbox] + label");
        // Play.
        await page.ClickAsync(".custom-game-options-play-button");
    }
    else
    {
        // Play.
        await page.ClickAsync(".new-game-margin-component > button");
    }

    // Wait for game to start by looking for the chat icon.
    while (!await page.IsVisibleAsync(".quick-chat-icon-bottom"))
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

Task HideHintsAsync(IPage page)
{
    return page.EvaluateAsync("document.head.insertAdjacentHTML('beforeend', '<style>.hint { background-color: transparent; }</style>')");
}

async Task RunGameAsync(IPage page, TwitchClient twitchClient)
{
    var votesChan = Channel.CreateUnbounded<Vote>();
    ListenForVotes(twitchClient, votesChan.Writer);

    var playerColor = await GetPlayerColorAsync(page);
    if (playerColor == PieceColor.Black)
    {
        await WaitForTurnAsync(page, playerColor);
    }

    do
    {
        if (await HasGameEndedAsync(page))
        {
            break;
        }

        Dictionary<string, Move> legalMoves = new(StringComparer.OrdinalIgnoreCase);
        foreach (var m in await ComputeLegalMoves(page, playerColor))
        {
            legalMoves[m.AlgebraicNotation.Replace("x", "")] = m;
        }

        if (legalMoves.Count == 1)
        {
            await ProcessMove(page, legalMoves.First().Value);
        }
        else
        {
            var ballotBox = await CollectVotesAsync(votesChan.Reader, legalMoves, page, playerColor);
            if (await HasGameEndedAsync(page))
            {
                break;
            }

            int moveVotes = ballotBox.Moves.Values.DefaultIfEmpty().Max();
            if (moveVotes >= ballotBox.Resign && moveVotes >= ballotBox.Draw)
            {
                Move winningMove = ComputeWinningMove(ballotBox.Moves);
                await ProcessMove(page, winningMove);
            }
            else if (ballotBox.Resign >= ballotBox.Draw && ballotBox.Resign >= moveVotes)
            {
                Console.WriteLine("Resigned");
                await ResignAsync(page);
            }
            else if (ballotBox.Draw >= ballotBox.Resign && ballotBox.Draw >= moveVotes)
            {
                Console.WriteLine("Offering draw");
                if (!await OfferDrawAsync(page))
                {
                    continue;
                }
            }
        }

        // Viewers could sometimes see that it's their turn but because of the stream delay, their vote is received too
        // late. So discard votes for a while at the end of a turn.
        await DiscardLateMovesAsync(votesChan.Reader);

        if (await HasGameEndedAsync(page))
        {
            break;
        }

        await WaitForTurnAsync(page, playerColor);
        Console.WriteLine("---------------------------------------------------"); // Mark the end of the turn.
    } while (true);
}

async Task WaitForTurnAsync(IPage page, PieceColor color)
{
    // Look for the two highlighted squared that represent a player move and find if they contain a white or black piece.

    PieceColor opponentColor = color == PieceColor.White ? PieceColor.Black : PieceColor.White;
    while (true)
    {
        if (await HasGameEndedAsync(page))
        {
            return;
        }

        var highlightElements = await page.QuerySelectorAllAsync("chess-board > .highlight");
        foreach (var highlightEl in highlightElements)
        {
            // TODO: check it's a yellow highlight (move).
            var highlightClassHandle = await highlightEl.GetPropertyAsync("className"); // Expected to be "highlight square-XY".
            var highlightClasses = (await highlightClassHandle.JsonValueAsync<string>()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (highlightClasses.Length == 0) // The element was removed?
            {
                continue;
            }

            var pieceEl = await page.QuerySelectorAsync("chess-board > .piece." + highlightClasses[1]);
            if (pieceEl != null && (await GetPieceInfoAsync(pieceEl))!.Piece.Color == opponentColor)
            {
                return;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

async Task<PieceColor> GetPlayerColorAsync(IPage page)
{
    var flippedBoardEl = await page.QuerySelectorAsync("chess-board.flipped");
    return flippedBoardEl == null ? PieceColor.White : PieceColor.Black;
}

async Task<bool> HasGameEndedAsync(IPage page)
{
    var gameOverModalEl = await page.QuerySelectorAsync(".modal-game-over-component");
    return gameOverModalEl != null;
}

async Task DiscardLateMovesAsync(ChannelReader<Vote> votesChan)
{
    int discardedVotes = 0;
    CancellationTokenSource cts = new(streamDelay);
    do
    {
        try
        {
            await votesChan.ReadAsync(cts.Token);
            discardedVotes += 1;
        }
        catch (OperationCanceledException)
        {
        }
    } while (!cts.IsCancellationRequested);

    if (discardedVotes != 0)
    {
        Console.WriteLine($"Discarded {discardedVotes} late votes");
    }
}

async Task<List<Move>> ComputeLegalMoves(IPage page, PieceColor color)
{
    List<Move> moves = new();

    var pieceElements = await page.QuerySelectorAllAsync(
        $"chess-board > .piece[class*=\" {(color == PieceColor.White ? 'w' : 'b')}\"]");
    foreach (IElementHandle pieceEl in pieceElements)
    {
        await GetLegalMoveForPieceAsync(page, pieceEl, moves);
    }

    return MovesToAlgebraicNotation(moves);
}

async Task<BallotBox> CollectVotesAsync(ChannelReader<Vote> votesChan,
    Dictionary<string, Move> legalMoves, IPage page, PieceColor playerColor)
{
    BallotBox ballotBox = new();
    do
    {
        HashSet<string> usernames = new(StringComparer.Ordinal);
        CancellationTokenSource voteStopCts = new(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var vote in votesChan.ReadAllAsync(voteStopCts.Token))
            {
                if (usernames.Contains(vote.Username))
                {
                    Console.WriteLine($"{vote.Username} already voted for this turn");
                    continue;
                }

                if (vote.Action == "(=)")
                {
                    Console.WriteLine($"{vote.Username} voted for a draw");
                    ballotBox.Draw += 1;
                }
                else if (Regex.IsMatch(vote.Action, "\\d-\\d")) // (e.g. 0-1)
                {
                    Console.WriteLine($"{vote.Username} voted to resign");
                    ballotBox.Resign += 1;
                }
                else
                {
                    if (!legalMoves.TryGetValue(vote.Action.Replace("x", ""), out Move? move))
                    {
                        Console.WriteLine($"{vote.Username} voted for an invalid move ({vote.Action})");
                        continue;
                    }

                    Console.WriteLine($"{vote.Username} voted for {move.AlgebraicNotation}");
                    if (!ballotBox.Moves.ContainsKey(move))
                    {
                        ballotBox.Moves[move] = 1;
                        await AddArrowAsync(page, move, playerColor);
                        await UpdateArrowsOpacity(page, ballotBox.Moves, legalMoves);
                    }
                    else
                    {
                        ballotBox.Moves[move] += 1;
                    }
                }

                usernames.Add(vote.Username);
            }
        }
        catch (OperationCanceledException) when (voteStopCts.IsCancellationRequested)
        {
        }
    } while (ballotBox.Count == 0 && !await HasGameEndedAsync(page));

    return ballotBox;
}

async Task AddArrowAsync(IPage page, Move move, PieceColor playerColor)
{
    var boardEl = (await page.QuerySelectorAsync("chess-board"))!;
    var pieceEl = await boardEl.QuerySelectorAsync(".piece" + XyToSquareClass(move.Src.x, move.Src.y));
    if (pieceEl == null)
    {
        return;
    }

    // If the player is playing black, the board is inverted.
    int color = playerColor == PieceColor.White ? 1 : -1;

    var pieceBoundingBox = (await pieceEl.BoundingBoxAsync())!;
    float pieceCenterX = pieceBoundingBox.X + pieceBoundingBox.Width / 2;
    float pieceCenterY = pieceBoundingBox.Y + pieceBoundingBox.Height / 2;
    float dstX = pieceCenterX + color * pieceBoundingBox.Width * (move.Dst.x - move.Src.x);
    float dstY = pieceCenterY - color * pieceBoundingBox.Height * (move.Dst.y - move.Src.y);
    await page.Mouse.MoveAsync(pieceCenterX, pieceCenterY);
    await page.Mouse.DownAsync(new MouseDownOptions { Button = MouseButton.Right });
    await page.Mouse.MoveAsync(dstX, dstY);
    await page.Mouse.UpAsync(new MouseUpOptions { Button = MouseButton.Right });
}

async Task UpdateArrowsOpacity(IPage page, Dictionary<Move, int> votes, Dictionary<string, Move> legalMoves)
{
    int maxVotes = votes.Values.Max();

    var arrowElements = await page.QuerySelectorAllAsync("chess-board .arrow");
    foreach (var arrowEl in arrowElements)
    {
        string dataArrowAttribute = (await arrowEl.GetAttributeAsync("data-arrow"))!;
        (int, int) src = (FileToX(dataArrowAttribute[0]), RankToY(dataArrowAttribute[1]));
        (int, int) dst = (FileToX(dataArrowAttribute[2]), RankToY(dataArrowAttribute[3]));

        var move = legalMoves.First(kvp => kvp.Value.Src == src && kvp.Value.Dst == dst);
        int votesForMove = votes[move.Value];
        float opacity = (float)votesForMove / maxVotes;

        await arrowElements[0].EvaluateAsync($"a => a.style.opacity = {opacity}");
    }
}

async Task ProcessMove(IPage page, Move move)
{
    Console.WriteLine($"Processing move {move.AlgebraicNotation}");
    var pieceEl = await page.QuerySelectorAsync("chess-board > .piece" + XyToSquareClass(move.Src.x, move.Src.y));
    await pieceEl!.ClickAsync();
    var hintEl = await page.QuerySelectorAsync("chess-board > .hint"
                                                + XyToSquareClass(move.Dst.x, move.Dst.y)
                                                + ", chess-board > .capture-hint"
                                                + XyToSquareClass(move.Dst.x, move.Dst.y));
    await hintEl!.ClickAsync(new ElementHandleClickOptions { Force = true });

    await PromotePawnIfPossibleAsync(page);
    await WaitForMoveAnimationAsync(page);
}

Move ComputeWinningMove(Dictionary<Move, int> moveVotes)
{
    KeyValuePair<Move, int>[] moveVotesCollection = moveVotes.OrderByDescending(v => v.Value).ToArray();
    int candidatesEndIdx = Array.FindLastIndex(moveVotesCollection, v => v.Value == moveVotesCollection[0].Value);
    return moveVotesCollection[Random.Shared.Next(candidatesEndIdx)].Key;
}

async Task PromotePawnIfPossibleAsync(IPage page)
{
    var promotionWindowEl = await page.QuerySelectorAsync("chess-board > .promotion-window");
    if (promotionWindowEl != null && await promotionWindowEl.IsVisibleAsync())
    {
        var queenPromotionEl = await promotionWindowEl.QuerySelectorAsync(".wq, .bq");
        await queenPromotionEl!.ClickAsync();
    }
}

async Task WaitForMoveAnimationAsync(IPage page)
{
    var board = (await page.QuerySelectorAsync("chess-board"))!;
    while (true)
    {
        try
        {
            await board.GetAttributeAsync("data-test-animating");
        }
        catch (KeyNotFoundException) // GetAttributeAsync throws if the attribute is not present :/
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100));
    }
}

Task ResignAsync(IPage page)
{
   return page.ClickAsync(".icon-font-chess.flag");
}

Task<bool> OfferDrawAsync(IPage page)
{
    return Task.FromResult(false);
}

async Task GetLegalMoveForPieceAsync(IPage page, IElementHandle pieceEl, List<Move> moves)
{
    var pieceInfo = await GetPieceInfoAsync(pieceEl);
    if (pieceInfo == null)
    {
        return;
    }

    if (!await pieceEl.IsVisibleAsync()) // For some reason, the pieces query can return a captured piece.
    {
        return;
    }

    await pieceEl.ClickAsync();

    var hintElements = await page.QuerySelectorAllAsync("chess-board > .hint, chess-board > .capture-hint");
    foreach (IElementHandle hintEl in hintElements)
    {
        var hintClassHandle = await hintEl.GetPropertyAsync("className"); // Expected to be "hint square-XY".
        string[] hintClasses = (await hintClassHandle.JsonValueAsync<string>()).Split(' ');
        bool capture = hintClasses[0].StartsWith("capture", StringComparison.Ordinal);
        int hintX = hintClasses[1][^2] - '0' - 1;
        int hintY = hintClasses[1][^1] - '0' - 1;

        moves.Add(new Move(pieceInfo.Piece, pieceInfo.Pos, (hintX, hintY), capture));
    }

    // Unselect piece.
    await pieceEl.ClickAsync(); // Force ?
}

// https://en.wikipedia.org/wiki/Algebraic_notation_(chess)
List<Move> MovesToAlgebraicNotation(List<Move> moves)
{
    foreach (var group in moves.GroupBy(m => (m.Piece.Type, m.Dst)))
    {
        var ambiguousMoves = group.ToArray();
        if (ambiguousMoves.Length == 1)
        {
            var m = ambiguousMoves[0];
            string not = (m.Capture ? "x" : "") + XyToFileRank(m.Dst.x, m.Dst.y);
            m.AlgebraicNotation = m.Piece.Type == PieceType.Pawn
                ? (m.Capture ? XToFile(m.Src.x).ToString() : "") + not
                : pieceTypeToLetter[m.Piece.Type] + not;
        }
        else
        {
            bool fileDiffers = ambiguousMoves.Any(m => m.Src.x != ambiguousMoves[0].Src.x);
            bool rankDiffers = ambiguousMoves.Any(m => m.Src.y != ambiguousMoves[0].Src.y);
            string not = (ambiguousMoves[0].Capture ? "x" : "") + XyToFileRank(ambiguousMoves[0].Dst.x, ambiguousMoves[0].Dst.y);
            foreach (var m in ambiguousMoves)
            {
                m.AlgebraicNotation = (m.Piece.Type != PieceType.Pawn ? pieceTypeToLetter[m.Piece.Type] : "")
                                      + (fileDiffers ? XToFile(m.Src.x).ToString() : "")
                                      + (!fileDiffers && rankDiffers ? YToRank(m.Src.y).ToString() : "")
                                      + not;
            }
        }
    }

    return moves;
}

async Task<PieceWithPos?> GetPieceInfoAsync(IElementHandle pieceEl)
{
    var classHandle = await pieceEl.GetPropertyAsync("className"); // Expected to be "piece wp square-XY".
    string[] classes = (await classHandle.JsonValueAsync<string>()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (classes.Length == 0) // Piece was captured and for some reason it was returned by the piece query.
    {
        return null;
    }

    string colorType = classes.First(c => c.Length == 2);
    var pieceType = pieceLetterToTypes[colorType[1]];
    var pieceColor = colorType[0] == 'w' ? PieceColor.White : PieceColor.Black;

    string squareClass = classes.First(c => c.StartsWith("square-", StringComparison.Ordinal));
    int x = squareClass[^2] - '0' - 1;
    int y = squareClass[^1] - '0' - 1;

    Piece piece = new(pieceColor, pieceType);
    return new PieceWithPos(piece, (x, y));
}

TwitchClient CreateTwitchClient()
{
    string twitchToken = Environment.GetEnvironmentVariable("TWITCH_TOKEN")
                         ?? throw new InvalidOperationException("TWITCH_TOKEN not set");
    TwitchClient twitchClient = new(new WebSocketClient());
    twitchClient.Initialize(new ConnectionCredentials(twitchUsername, twitchToken),
        twitchUsername);

    twitchClient.Connect();
    return twitchClient;
}

void ListenForVotes(TwitchClient twitchClient, ChannelWriter<Vote> votesChan)
{
    twitchClient.OnMessageReceived += (_, e) =>
    {
        if (!e.ChatMessage.Message.StartsWith('!'))
        {
            return;
        }

        var vote = new Vote(e.ChatMessage.Message.TrimStart('!'), e.ChatMessage.Username);
        votesChan.TryWrite(vote);
    };
}

string XyToFileRank(int x, int y) => XToFile(x).ToString() + YToRank(y);
char XToFile(int x) => (char)(x + 'a');
char YToRank(int y) => (char)(y + '0' + 1);
int FileToX(char file) => file - 'a';
int RankToY(char rank) => rank - '0' - 1;
string XyToSquareClass(int x, int y) => ".square-" + (x + 1) + (y + 1);

record Move(Piece Piece, (int x, int y) Src, (int x, int y) Dst, bool Capture)
{
    public string AlgebraicNotation { get; set; } = string.Empty;
}

record Piece(PieceColor Color, PieceType Type);

record PieceWithPos(Piece Piece, (int x, int y) Pos);

enum PieceColor
{
    White,
    Black,
}

enum PieceType
{
    King,
    Queen,
    Rook,
    Bishop,
    Knight,
    Pawn,
}

record Vote(string Action, string Username);

record BallotBox
{
    public Dictionary<Move, int> Moves { get; } = new();
    public int Resign { get; set; }
    public int Draw { get; set; }

    public int Count => Moves.Count + Resign + Draw;
}
