using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FlexibleJsonConvertersTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record TestIntModel(
        [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? Page,
        [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int Seconds
    );

    private sealed record TestDecimalModel(
        [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
        [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Servings
    );

    [TestMethod]
    public void FlexibleNullableIntJsonConverter_ParsesIntegersAndStrings()
    {
        // 1. Direct integer
        var m1 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": 184, \"seconds\": 60}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(184, m1?.Page);
        Assert.AreEqual(60, m1?.Seconds);

        // 2. Direct string integer
        var m2 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"184\", \"seconds\": \"60\"}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(184, m2?.Page);
        Assert.AreEqual(60, m2?.Seconds);

        // 3. String with page prefix
        var m3 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"page 184\", \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(184, m3?.Page);

        // 4. String with p. prefix
        var m4 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"p. 184\", \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(184, m4?.Page);

        // 5. String range "184-185"
        var m5 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"184-185\", \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(184, m5?.Page);

        // 6. String empty or N/A or null
        var m6 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"N/A\", \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.IsNull(m6?.Page);

        var m7 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": \"\", \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.IsNull(m7?.Page);

        var m8 = JsonSerializer.Deserialize<TestIntModel>(
            "{\"page\": null, \"seconds\": 0}",
            CaseInsensitiveOptions
        );
        Assert.IsNull(m8?.Page);
    }

    [TestMethod]
    public void FlexibleNullableDecimalJsonConverter_ParsesDecimalsFractionsAndUnicode()
    {
        // 1. Direct number
        var m1 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": 1.5, \"servings\": 4}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(1.5m, m1?.Quantity);
        Assert.AreEqual(4m, m1?.Servings);

        // 2. String number
        var m2 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"1.5\", \"servings\": \"4\"}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(1.5m, m2?.Quantity);
        Assert.AreEqual(4m, m2?.Servings);

        // 3. Simple fraction "1/2"
        var m3 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"1/2\", \"servings\": 4}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(0.5m, m3?.Quantity);

        // 4. Mixed fraction "1 1/2"
        var m4 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"1 1/2\", \"servings\": 4}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(1.5m, m4?.Quantity);

        // 5. Unicode fraction "½"
        var m5 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"½\", \"servings\": 4}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(0.5m, m5?.Quantity);

        // 6. Unicode mixed fraction "1½"
        var m6 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"1½\", \"servings\": 4}",
            CaseInsensitiveOptions
        );
        Assert.AreEqual(1.5m, m6?.Quantity);

        // 7. Null and empty
        var m7 = JsonSerializer.Deserialize<TestDecimalModel>(
            "{\"quantity\": \"none\", \"servings\": null}",
            CaseInsensitiveOptions
        );
        Assert.IsNull(m7?.Quantity);
        Assert.IsNull(m7?.Servings);
    }
}
