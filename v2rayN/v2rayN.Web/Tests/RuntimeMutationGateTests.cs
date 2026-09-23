using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RuntimeMutationGateTests
{
    [Test]
    public async Task ConcurrentConfigWritesSerializeTheFixedTemporaryFile()
    {
        var gate = new RuntimeMutationGate();
        var configPath = Path.Combine(Path.GetTempPath(), $"v2rayn-web-config-{Guid.NewGuid():N}.json");
        var tempPath = $"{configPath}_temp";

        try
        {
            var writers = Enumerable.Range(0, 24).Select(value => gate.RunAsync(async () =>
            {
                await File.WriteAllTextAsync(tempPath, $"{{\"value\":{value}}}");
                await Task.Delay(2);
                File.Move(tempPath, configPath, overwrite: true);
            }));

            await Task.WhenAll(writers);

            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var value = document.RootElement.GetProperty("value").GetInt32();
            await (value is >= 0 and <= 23).Should().BeTrue();
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(tempPath);
        }
    }

    [Test]
    public async Task SaveFailureCannotBeTreatedAsSuccess()
    {
        var failed = false;
        try
        {
            await V2rayRuntime.EnsureConfigSaveSucceededAsync(() => Task.FromResult(-1));
        }
        catch (IOException)
        {
            failed = true;
        }

        await failed.Should().BeTrue();
    }
}
