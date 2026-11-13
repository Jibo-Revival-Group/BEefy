using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class SpeechTranslationProtocolTests
{
    private const string Robot = "Test-Robot-One-Two";

    [Fact]
    public async Task TranslateSpeech_ReturnsTheSpeechPartsForTheRobotsLanguage()
    {
        var cloud = new InMemoryCloudStateStore();
        var translator = new FakeTranslator();
        var service = new JiboCloudProtocolService(cloud, speechTranslator: translator);

        var ok = await service.DispatchAsync(Signed(cloud, new { language = "ES", text = "<speak>hello .</speak>" }));

        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(("es", "<speak>hello .</speak>"), translator.Last);
        using var body = JsonDocument.Parse(ok.BodyText!);
        Assert.Equal("Hola.", body.RootElement.GetProperty("translation").GetString());
        Assert.Equal("<phoneme ph='o1 l aa0'>word</phoneme>.", body.RootElement.GetProperty("parts")[0].GetString());
    }

    [Fact]
    public async Task TranslateSpeech_RefusesUnsignedBadAndUntranslatableRequests()
    {
        var cloud = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(cloud, speechTranslator: new FakeTranslator());

        var unsigned = Signed(cloud, new { language = "es", text = "hello" });
        unsigned.Headers.Remove("Authorization");
        Assert.Equal(401, (await service.DispatchAsync(unsigned)).StatusCode);
        Assert.Equal(400, (await service.DispatchAsync(Signed(cloud, new { language = "es" }))).StatusCode);
        Assert.Equal(400, (await service.DispatchAsync(Signed(cloud, new { language = "es", text = new string('a', 2001) }))).StatusCode);
        // A language the server does not offer: the robot keeps speaking English.
        Assert.Equal(503, (await service.DispatchAsync(Signed(cloud, new { language = "xx", text = "hello" }))).StatusCode);
        Assert.Equal(503, (await new JiboCloudProtocolService(cloud).DispatchAsync(Signed(cloud, new { language = "es", text = "hello" }))).StatusCode);
    }

    [Fact]
    public async Task LocalSpeechTranslator_TalksToItsWorkerOverOneLinePerRequest()
    {
        var python = new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);
        if (python is null) return; // no Python on this machine
        var directory = Directory.CreateTempSubdirectory("speech-translation-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "en-es"));
            File.WriteAllText(Path.Combine(directory, "en-es", "model.bin"), "");
            var worker = Path.Combine(directory, "worker.py");
            // Stand-in for the real worker: same protocol, no models.
            File.WriteAllText(worker, """
                import json, sys
                print(json.dumps({"ready": True}), flush=True)
                for line in sys.stdin:
                    r = json.loads(line)
                    ok = r["language"] == "es"
                    print(json.dumps({"id": r["id"], "result": {"translation": r["text"].upper(), "parts": ["<p>" + r["text"] + "</p>"]} if ok else None}), flush=True)
                """.Replace("                ", ""));
            using var translator = new LocalSpeechTranslator(
                new SpeechTranslationOptions { Directory = directory, PythonPath = python, WorkerPath = worker },
                NullLogger<LocalSpeechTranslator>.Instance);
            Assert.Null(await translator.TryTranslateAsync("es", "hello")); // not started yet
            await translator.StartAsync(CancellationToken.None);
            for (var i = 0; i < 100 && !translator.IsReady; i++) await Task.Delay(50);

            var speech = await translator.TryTranslateAsync("es", "hello");
            Assert.Equal("HELLO", speech?.Translation);
            Assert.Equal(["<p>hello</p>"], speech?.Parts);
            Assert.Null(await translator.TryTranslateAsync("xx", "hello"));
            Assert.Equal("AGAIN", (await translator.TryTranslateAsync("es", "again"))?.Translation); // the pipe stays aligned
            await translator.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ProtocolEnvelope Signed(InMemoryCloudStateStore cloud, object body)
    {
        var account = cloud.GetAccount();
        var bodyText = JsonSerializer.Serialize(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "api.jibo.com",
            ["X-Amz-Date"] = "Sat, 03 Oct 2026 13:00:00 GMT",
            ["X-Amz-Target"] = "Loop_20160324.TranslateSpeech"
        };
        headers["Authorization"] = Aws3Signature.BuildAuthorization(account.AccessKeyId, account.SecretAccessKey, "POST", headers, bodyText);
        return new ProtocolEnvelope
        {
            Method = "POST", HostName = "api.jibo.com", ServicePrefix = "Loop_20160324", Operation = "TranslateSpeech",
            DeviceId = Robot, BodyText = bodyText, Headers = headers
        };
    }

    private sealed class FakeTranslator : ISpeechTranslator
    {
        public (string, string) Last;

        public Task<TranslatedSpeech?> TryTranslateAsync(string language, string text, CancellationToken cancellationToken = default)
        {
            Last = (language, text);
            return Task.FromResult(language == "es"
                ? new TranslatedSpeech("Hola.", ["<phoneme ph='o1 l aa0'>word</phoneme>."])
                : null);
        }
    }
}
