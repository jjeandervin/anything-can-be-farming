using System.Net;
using System.Text.Json;
using AnythingCanBeFarming.Api.Identification;
using AnythingCanBeFarming.Api.PlantNet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnythingCanBeFarming.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/identify")]
public sealed class IdentifyController(PlantNetClient plantNet, ILogger<IdentifyController> logger) : ControllerBase
{
    private const int MaxImages = 5;
    private const long MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxRequestBytes = 30 * 1024 * 1024;
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<IdentifyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<IdentifyError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<IdentifyError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<IdentifyError>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<IdentifyError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Identify()
    {
        var cancellationToken = HttpContext.RequestAborted;
        // Read the form here rather than through [FromForm] binding, so an unreadable or oversized body
        // still gets this endpoint's error shape instead of the framework's automatic 400.
        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Invalid("image_too_large", "These photos are too large to send together. Use fewer photos.");
        }
        catch (Exception error) when (error is InvalidDataException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return Invalid("no_images", "The upload couldn't be read. Add your photos and try again.");
        }
        var images = form.Files.GetFiles("images");
        var organs = form["organs"];
        if (images.Count == 0)
            return Invalid("no_images", "Add at least one photo.");
        if (images.Count > MaxImages)
            return Invalid("too_many_images", $"Use at most {MaxImages} photos per identification.");
        if (organs.Count != images.Count)
            return Invalid("organ_count_mismatch", "Supply exactly one organ per photo.");
        var organWireValues = new List<string>(organs.Count);
        foreach (var value in organs)
        {
            if (!PlantOrgans.TryParse(value, out var organ))
                return Invalid("invalid_organ", "One of the organ values isn't recognized.");
            organWireValues.Add(PlantOrgans.ToWire(organ));
        }

        var uploads = new List<PlantNetImageUpload>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var file = images[index];
            if (file.Length == 0)
                return Invalid("empty_image", $"Photo {index + 1} is empty.");
            if (file.Length > MaxImageBytes)
                return Invalid("image_too_large", $"Photo {index + 1} is larger than 8 MB.");
            var content = new byte[file.Length];
            await using (var stream = file.OpenReadStream())
                await stream.ReadExactlyAsync(content, cancellationToken);
            // Trust only the bytes, never the client's content type or file name.
            var (contentType, extension) = content.AsSpan().StartsWith(JpegMagic) ? ("image/jpeg", "jpg")
                : content.AsSpan().StartsWith(PngMagic) ? ("image/png", "png")
                : (null, null);
            if (contentType is null)
                return Invalid("unsupported_image", $"Photo {index + 1} must be a JPEG or PNG image.");
            // Index-based names let predicted organs be mapped back to photos.
            uploads.Add(new PlantNetImageUpload(content, $"image-{index}.{extension}", contentType));
        }

        PlantNetIdentificationResult result;
        try
        {
            result = await plantNet.IdentifyAsync(uploads, "all", new PlantNetPlantIdentificationOptions
            {
                Language = "en",
                IncludeRelatedImages = true,
                Organs = organWireValues
            }, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Plant identification requested but PlantNet:ApiKey is not configured.");
            return Failure(StatusCodes.Status503ServiceUnavailable, "identification_unavailable",
                "Plant identification isn't configured.");
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // Pl@ntNet answers 404 when it finds no plant. That is a valid answer, not an error.
            return Ok(IdentifyResponse.NoMatch);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning("Pl@ntNet identification quota exhausted.");
            return Failure(StatusCodes.Status429TooManyRequests, "quota_exceeded",
                "Daily identification limit reached. Try again tomorrow.");
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType)
        {
            logger.LogWarning("Pl@ntNet rejected an identification request with HTTP {StatusCode}.", (int)error.StatusCode!);
            return Failure(StatusCodes.Status502BadGateway, "upstream_rejected", "Pl@ntNet couldn't process these photos.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException ||
            (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Covers other upstream statuses, transport failures, bad JSON, and the HttpClient timeout.
            // A cancellation caused by the caller disconnecting is not caught and propagates.
            logger.LogWarning("Pl@ntNet identification failed: {ErrorType} (HTTP {StatusCode}).",
                error.GetType().Name, (int?)(error as HttpRequestException)?.StatusCode);
            return Failure(StatusCodes.Status502BadGateway, "upstream_error",
                "Plant identification is temporarily unavailable.");
        }

        return Ok(IdentifyResponse.From(result, uploads.Select(upload => upload.FileName).ToList()));
    }

    private BadRequestObjectResult Invalid(string code, string message) => BadRequest(new IdentifyError(code, message));

    private ObjectResult Failure(int status, string code, string message) =>
        StatusCode(status, new IdentifyError(code, message));
}
