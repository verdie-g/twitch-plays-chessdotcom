using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Playwright;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;

const string chessDotComUserName = "twitch_plays_chessdot";
const string twitchUsername = "twitch_plays_chessdotcom";

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

int viewers = 0;

TwitchClient twitch = CreateTwitchClient();
ListenForUserJoinLeft(twitch);

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
    await StartOfflineGameAsync(chessPage);
    // await StartOnlineGameAsync(page);
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

async Task StartOfflineGameAsync(IPage page)
{
    await page.GotoAsync("home");

    // Play against computer.
    await page.RunAndWaitForNavigationAsync(() => page.ClickAsync("#quick-link-computer"),
        new PageRunAndWaitForNavigationOptions { UrlString = "**/play/computer" });

    // Select opponent.
    await page.ClickAsync(".selection-menu-footer > button");

    // Play.
    await page.ClickAsync(".selection-menu-footer > button");
}

async Task StartOnlineGameAsync(IPage page)
{
    await page.GotoAsync("home");

    // New Game.
    await page.RunAndWaitForNavigationAsync(() => page.ClickAsync("#quick-link-new_game"),
        new PageRunAndWaitForNavigationOptions { UrlString = "**/play/online" });

    // Play.
    await page.ClickAsync(".new-game-margin-component > button");
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
            var moveVotes = await CollectVotesAsync(votesChan.Reader, legalMoves, twitchClient, page);
            if (await HasGameEndedAsync(page))
            {
                break;
            }

            Console.WriteLine("---------------------------------------------------"); // Mark the end of the turn.
            string moveStr = moveVotes.OrderByDescending(v => v.Value).First().Key;
            await ProcessMove(page, legalMoves[moveStr]);
        }

        if (await HasGameEndedAsync(page))
        {
            break;
        }

        await WaitForTurnAsync(page, playerColor);
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

async Task<Dictionary<string, int>> CollectVotesAsync(ChannelReader<Vote> votesChan,
    Dictionary<string, Move> legalMoves, TwitchClient twitchClient, IPage page)
{
    Dictionary<string, int> moveVotes = new(StringComparer.OrdinalIgnoreCase);
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
                    twitchClient.SendWhisper(vote.Username, "You already voted for this turn.");
                    continue;
                }

                if (!legalMoves.ContainsKey(vote.Move))
                {
                    twitchClient.SendWhisper(vote.Username, "Invalid move.");
                    continue;
                }

                Console.WriteLine($"{vote.Username} voted for {vote.Move}");
                string normalizedMove = vote.Move.Replace("x", "");
                if (!moveVotes.ContainsKey(normalizedMove))
                {
                    moveVotes[normalizedMove] = 1;
                    await AddArrowAsync(page, legalMoves[vote.Move]);
                    await UpdateArrowsOpacity(page, moveVotes, legalMoves);
                }
                else
                {
                    moveVotes[normalizedMove] += 1;
                }

                usernames.Add(vote.Username);
            }
        }
        catch (OperationCanceledException) when (voteStopCts.IsCancellationRequested)
        {
        }
    } while (moveVotes.Count == 0 && !await HasGameEndedAsync(page));

    return moveVotes;
}

async Task AddArrowAsync(IPage page, Move move)
{
    var boardEl = (await page.QuerySelectorAsync("chess-board"))!;
    var pieceEl = await boardEl.QuerySelectorAsync(".piece" + XyToSquareClass(move.Src.x, move.Src.y));
    if (pieceEl == null)
    {
        return;
    }

    var pieceBoundingBox = (await pieceEl.BoundingBoxAsync())!;
    float pieceCenterX = pieceBoundingBox.X + pieceBoundingBox.Width / 2;
    float pieceCenterY = pieceBoundingBox.Y + pieceBoundingBox.Height / 2;
    float dstX = pieceCenterX + pieceBoundingBox.Width * (move.Dst.x - move.Src.x);
    float dstY = pieceCenterY - pieceBoundingBox.Height * (move.Dst.y - move.Src.y);
    await page.Mouse.MoveAsync(pieceCenterX, pieceCenterY);
    await page.Mouse.DownAsync(new MouseDownOptions { Button = MouseButton.Right });
    await page.Mouse.MoveAsync(dstX, dstY);
    await page.Mouse.UpAsync(new MouseUpOptions { Button = MouseButton.Right });
}

async Task UpdateArrowsOpacity(IPage page, Dictionary<string, int> votes, Dictionary<string, Move> legalMoves)
{
    int maxVotes = votes.Values.Max();

    var arrowElements = await page.QuerySelectorAllAsync("chess-board .arrow");
    foreach (var arrowEl in arrowElements)
    {
        string dataArrowAttribute = (await arrowEl.GetAttributeAsync("data-arrow"))!;
        (int, int) src = (FileToX(dataArrowAttribute[0]), RankToY(dataArrowAttribute[1]));
        (int, int) dst = (FileToX(dataArrowAttribute[2]), RankToY(dataArrowAttribute[3]));

        var move = legalMoves.First(kvp => kvp.Value.Src == src && kvp.Value.Dst == dst);
        int votesForMove = votes[move.Value.AlgebraicNotation.Replace("x", "")];
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
                ? not
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

void ListenForUserJoinLeft(TwitchClient twitchClient)
{
    twitchClient.OnUserJoined += (_, _) => viewers += 1;
    twitchClient.OnUserLeft += (_, _) => viewers -= 1;
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

record Vote(string Move, string Username);
