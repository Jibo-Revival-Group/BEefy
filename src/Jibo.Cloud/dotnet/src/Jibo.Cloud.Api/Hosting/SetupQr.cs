using QRCoder;

namespace Jibo.Cloud.Api.Hosting;

internal static class SetupQr
{
    internal static string Render(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(4);
    }
}
