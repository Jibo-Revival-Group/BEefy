using System.Net.Http.Headers;
using System.Text;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>
/// Forwards Update_* calls to joap and returns the body unchanged, including
/// package URLs. A down joap becomes UPDATE_NOT_FOUND so the robot keeps going.
/// </summary>
internal sealed class JoapUpdateProxy(IHttpClientFactory httpClientFactory, ILogger<JoapUpdateProxy> logger)
{
    internal const string ClientName = "JoapUpdate";
    internal const string BaseUrl = "http://joap.5x1.com:80";

    internal async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            var target = new Uri(new Uri(BaseUrl), context.Request.Path + context.Request.QueryString);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            if (context.Request.ContentLength is > 0 || context.Request.Body.CanRead)
            {
                context.Request.EnableBuffering();
                context.Request.Body.Position = 0;
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, cancellationToken);
                request.Content = new ByteArrayContent(buffer.ToArray());
                if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
                    request.Content.Headers.ContentType = contentType;
            }

            CopyHeader(context, request, "X-Amz-Target");
            CopyHeader(context, request, "Authorization");
            CopyHeader(context, request, "X-Amz-Date");
            CopyHeader(context, request, "X-Amz-Security-Token");
            CopyHeader(context, request, "X-Amz-Content-Sha256");
            CopyHeader(context, request, "X-Jibo-RobotId");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is not null)
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            if (response.Headers.TryGetValues("x-amzn-errortype", out var errorTypes))
                context.Response.Headers["x-amzn-errortype"] = errorTypes.FirstOrDefault();

            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Update forward to joap failed; answering UPDATE_NOT_FOUND");
            await WriteUpdateNotFoundAsync(context, cancellationToken);
        }
    }

    private static void CopyHeader(HttpContext context, HttpRequestMessage request, string name)
    {
        if (!context.Request.Headers.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            return;
        if (!request.Headers.TryAddWithoutValidation(name, value.ToString()))
            request.Content?.Headers.TryAddWithoutValidation(name, value.ToString());
    }

    private static Task WriteUpdateNotFoundAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/x-amz-json-1.1";
        context.Response.Headers["x-amzn-errortype"] = "UPDATE_NOT_FOUND";
        var body = """{"__type":"UPDATE_NOT_FOUND","code":"UPDATE_NOT_FOUND","message":"Update not found"}""";
        return context.Response.WriteAsync(body, Encoding.UTF8, cancellationToken);
    }
}
