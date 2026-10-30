using AniMeido.Contracts.DragDrop;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class AnimeCardDragPayloadSerializerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"animeId":42}""")]
    [InlineData("""{"schemaVersion":1,"animeId":42}""")]
    [InlineData("""{"kind":"AniMeido.AnimeCard","animeId":42}""")]
    [InlineData("""{"kind":"AniMeido.AnimeCard","schemaVersion":1}""")]
    [InlineData("""{"kind":"AniMeido.AnimeCard","schemaVersion":1,"animeId":0}""")]
    [InlineData("""{"kind":"Other.Card","schemaVersion":1,"animeId":42}""")]
    [InlineData("""{"kind":"AniMeido.AnimeCard","schemaVersion":0,"animeId":42}""")]
    [InlineData("""{"kind":"AniMeido.AnimeCard","schemaVersion":-1,"animeId":42}""")]
    [InlineData("null")]
    [InlineData("{broken")]
    public void Deserialize_MissingOrInvalidCardIdentityReturnsNull(string json)
    {
        Assert.Null(AnimeCardDragPayloadSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData(42, 1)]
    [InlineData(-42, 1)]
    [InlineData(42, 2)]
    [InlineData(-42, 2)]
    public void Deserialize_ExplicitIdentityKeepsPublicAndLocalIdsAndForwardCompatibleFields(
        int animeId,
        int schemaVersion)
    {
        var json = $$"""
            {
              "kind": "AniMeido.AnimeCard",
              "schemaVersion": {{schemaVersion}},
              "animeId": {{animeId}},
              "title": "合成作品",
              "futureField": {"enabled": true}
            }
            """;

        var payload = AnimeCardDragPayloadSerializer.Deserialize(json);

        Assert.NotNull(payload);
        Assert.Equal("AniMeido.AnimeCard", payload.Kind);
        Assert.Equal(schemaVersion, payload.SchemaVersion);
        Assert.Equal(animeId, payload.AnimeId);
        Assert.Equal("合成作品", payload.Title);
    }

    [Fact]
    public void Deserialize_ExplicitPropertyNamesRemainCaseInsensitive()
    {
        const string json = """{"KIND":"AniMeido.AnimeCard","SCHEMAVERSION":1,"ANIMEID":-42}""";

        var payload = AnimeCardDragPayloadSerializer.Deserialize(json);

        Assert.NotNull(payload);
        Assert.Equal(-42, payload.AnimeId);
        Assert.Equal(1, payload.SchemaVersion);
    }

    [Fact]
    public void Serialize_DefaultCardIdentityAndCanonicalFieldsRemainUnchanged()
    {
        var payload = new AnimeCardDragPayload
        {
            AnimeId = -42,
            Title = "合成作品",
        };

        var json = AnimeCardDragPayloadSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("AniMeido.AnimeCard", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(-42, root.GetProperty("animeId").GetInt32());
        Assert.Equal("合成作品", root.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("coverImageUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("summary").ValueKind);
        Assert.Equal(0, root.GetProperty("seasonYear").GetInt32());
        Assert.Equal(0, root.GetProperty("seasonMonth").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("source").ValueKind);
        Assert.NotNull(AnimeCardDragPayloadSerializer.Deserialize(json));
    }
}
