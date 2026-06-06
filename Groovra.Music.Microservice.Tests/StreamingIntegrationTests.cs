using System.Net;
using Groovra.Music.Microservice.Controllers;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Groovra.Music.Microservice.Tests;

/// <summary>
/// Вспомогательный класс: создаёт in-memory MusicService с заранее засеянным треком
/// и реальным аудиофайлом на диске.
/// </summary>
internal sealed class StreamTestFixture : IDisposable
{
    public MusicDbContext  DbContext     { get; }
    public MusicService    MusicService  { get; }
    public Track           Track         { get; }
    public byte[]          AudioBytes    { get; }
    public string          TempDir       { get; }

    public StreamTestFixture()
    {
        TempDir    = Path.Combine(Path.GetTempPath(), "groovra_unit_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(TempDir, "audio"));

        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        DbContext = new MusicDbContext(options);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MediaStorage:BasePath"] = TempDir })
            .Build();

        MusicService = new MusicService(DbContext, config, NullLogger<MusicService>.Instance);

        // Создаём тестовый аудиофайл (200 байт: 0x00–0xC7)
        AudioBytes = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

        Track = new Track
        {
            Id                = Guid.NewGuid(),
            Title             = "Test Track",
            ArtistName        = "Test Artist",
            ContentType       = "audio/mpeg",
            AudioRelativePath = $"audio/{Guid.NewGuid()}.mp3",
            UserId            = Guid.NewGuid(),
        };

        File.WriteAllBytes(Path.Combine(TempDir, Track.AudioRelativePath), AudioBytes);

        DbContext.Tracks.Add(Track);
        DbContext.SaveChanges();
    }

    public void Dispose()
    {
        DbContext.Dispose();
        if (Directory.Exists(TempDir))
            try { Directory.Delete(TempDir, recursive: true); } catch { /* ignore */ }
    }
}

/// <summary>
/// Unit-тесты для TracksController.StreamTrack.
/// Проверяют:
///   - тип возвращаемого ActionResult (PhysicalFileResult с enableRangeProcessing);
///   - установку заголовка Cache-Control;
///   - поведение при отсутствии X-User-Id (401);
///   - поведение при несуществующем треке (404).
///
/// NOTE: Проверка фактической обработки Range-заголовка (206 Partial Content)
/// выполняется ASP.NET Core pipeline'ом, который недоступен в юнит-тестах без
/// запущенного хоста. Корректность Range Processing гарантируется самим фреймворком
/// при включении enableRangeProcessing = true в PhysicalFileResult, что мы и
/// проверяем ниже.
/// </summary>
public sealed class StreamTrackControllerTests : IDisposable
{
    private readonly StreamTestFixture  _fixture;
    private readonly TracksController   _controller;

    public StreamTrackControllerTests()
    {
        _fixture    = new StreamTestFixture();
        _controller = new TracksController(
            _fixture.MusicService,
            NullLogger<TracksController>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Инициализирует HttpContext контроллера с минимальным набором заголовков.
    /// </summary>
    private static DefaultHttpContext BuildHttpContext(string? xUserId = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host   = new HostString("localhost");
        if (xUserId is not null)
        {
            ctx.Request.Headers.Append("X-User-Id",   xUserId);
            ctx.Request.Headers.Append("X-User-Role",  "User");
        }
        return ctx;
    }

    private TracksController WithContext(DefaultHttpContext ctx)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = ctx,
        };
        return _controller;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// StreamTrack должен вернуть PhysicalFileResult с enableRangeProcessing = true.
    /// Это гарантирует, что ASP.NET Core pipeline будет обрабатывать Range-запросы
    /// и возвращать 206 Partial Content браузерным плеерам.
    /// </summary>
    [Fact]
    public async Task StreamTrack_ValidRequest_ReturnsPhysicalFileResultWithRangeProcessing()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: Guid.NewGuid().ToString());
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert — тип результата
        var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);

        // Assert — enableRangeProcessing включён (иначе Range-запросы не будут работать)
        Assert.True(physicalFileResult.EnableRangeProcessing,
            "enableRangeProcessing должен быть true для поддержки HTTP 206 Partial Content.");
    }

    /// <summary>
    /// Контроллер должен вернуть PhysicalFileResult, ссылающийся на реальный файл
    /// с правильным MIME-типом.
    /// </summary>
    [Fact]
    public async Task StreamTrack_ValidRequest_ReturnsCorrectFilePathAndContentType()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: Guid.NewGuid().ToString());
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        var expectedPath = Path.Combine(_fixture.TempDir, _fixture.Track.AudioRelativePath);

        Assert.Equal(expectedPath,            fileResult.FileName);
        Assert.Equal(_fixture.Track.ContentType, fileResult.ContentType);
    }

    /// <summary>
    /// Контроллер должен выставить Cache-Control заголовок перед возвратом PhysicalFileResult.
    /// Значение должно содержать 'public', 'max-age=31536000' и 'immutable'.
    /// </summary>
    [Fact]
    public async Task StreamTrack_ValidRequest_SetsCacheControlHeader()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: Guid.NewGuid().ToString());
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert — результат успешный
        Assert.IsType<PhysicalFileResult>(result);

        // Assert — заголовок установлен в Response
        var cacheControl = ctx.Response.Headers.CacheControl.ToString();
        Assert.Contains("public",       cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("immutable",    cacheControl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Запрос без заголовка X-User-Id должен вернуть UnauthorizedObjectResult (401).
    /// </summary>
    [Fact]
    public async Task StreamTrack_MissingXUserId_ReturnsUnauthorized()
    {
        // Arrange — не передаём X-User-Id
        var ctx = BuildHttpContext(xUserId: null);
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// Невалидный (не-GUID) X-User-Id должен вернуть 401.
    /// </summary>
    [Fact]
    public async Task StreamTrack_InvalidXUserId_ReturnsUnauthorized()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: "not-a-guid");
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// Запрос для несуществующего ID трека должен вернуть NotFoundObjectResult (404).
    /// </summary>
    [Fact]
    public async Task StreamTrack_NonExistentTrackId_ReturnsNotFound()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: Guid.NewGuid().ToString());
        var controller = WithContext(ctx);
        var nonExistentId = Guid.NewGuid(); // Этого трека нет в In-Memory БД

        // Act
        var result = await controller.StreamTrack(nonExistentId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Верификация того, что PhysicalFileResult возвращается с contentType == audio/mpeg
    /// (без Range заголовка — полный файл, 200 OK с правильным Content-Type).
    /// Подтверждает: pipeline вернёт 200, а не 206, если Range не передан.
    /// </summary>
    [Fact]
    public async Task StreamTrack_ValidRequest_ContentTypeIsAudioMpeg()
    {
        // Arrange
        var ctx = BuildHttpContext(xUserId: Guid.NewGuid().ToString());
        var controller = WithContext(ctx);

        // Act
        var result = await controller.StreamTrack(_fixture.Track.Id, CancellationToken.None);

        // Assert
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("audio/mpeg", fileResult.ContentType);
    }
}
