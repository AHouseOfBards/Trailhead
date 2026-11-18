using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Campground.Shared.I18n;
using Jellyfin.Plugin.Trailhead.Api;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

[Collection(RegistryCollection.Name)]
public sealed class StringsTests : ApiTestBase
{
    private static Strings Embedded() => Strings.Load(typeof(Plugin).Assembly, UiText.Prefix);

    [Fact]
    public void The_embedded_tables_load_and_validate_clean()
    {
        var strings = Embedded();

        Assert.Contains("en", strings.Languages);
        Assert.NotEmpty(strings.TableFor("en"));
        Assert.Empty(strings.Validate());
    }

    [Fact]
    public void A_set_of_tables_with_no_English_is_not_clean()
    {
        var strings = Strings.FromJson(new Dictionary<string, string> { ["de"] = "{\"a\":\"b\"}" });

        Assert.NotEmpty(strings.Validate());
    }

    [Fact]
    public void A_translation_that_drops_a_key_or_a_placeholder_is_caught()
    {
        var en = JsonSerializer.Serialize(UiText.Table.TableFor("en"));
        var xx = JsonSerializer.Serialize(new Dictionary<string, string> { ["label.tooShort"] = "no placeholder" });

        var problems = Strings.FromJson(new Dictionary<string, string> { ["en"] = en, ["xx"] = xx }).Validate();

        Assert.Contains(problems, p => p.Language == "xx" && p.Key == "label.tooShort" && p.Problem.Contains("placeholders"));
        Assert.Contains(problems, p => p.Language == "xx" && p.Key == "name.kind.taken");
    }

    [Fact]
    public void Every_kind_problem_and_error_code_has_English_text_and_no_key_is_missing_a_user_of_the_enums()
    {
        var kinds = Enum.GetValues<ClaimKind>();
        var problems = Enum.GetValues<LabelProblem>().Where(p => p != LabelProblem.None).ToList();
        var codes = typeof(ApiCodes).GetFields(BindingFlags.NonPublic | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.NotEmpty(kinds);
        Assert.NotEmpty(problems);
        Assert.NotEmpty(codes);
        var en = UiText.Table.TableFor("en");
        foreach (var k in kinds)
        {
            Assert.True(en.ContainsKey(NameClaimer.KeyFor(k)), "no text for ClaimKind." + k);
        }

        foreach (var p in problems)
        {
            Assert.True(en.ContainsKey(HostLabel.KeyFor(p)), "no text for LabelProblem." + p);
        }

        foreach (var c in codes)
        {
            Assert.True(en.ContainsKey("error." + c), "no text for error code " + c);
        }

        Assert.True(en.ContainsKey("dynuKey.reenter"));
    }

    [Fact]
    public async Task The_strings_route_gives_the_merged_table_with_an_ETag_and_a_304_for_a_repeat()
    {
        using var first = await GetAsync("/Trailhead/strings/en");
        var etag = first.Headers.ETag!.ToString();
        using var json = await JsonAsync(first);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(UiText.English("name.kind.taken"), json.RootElement.GetProperty("name.kind.taken").GetString());
        Assert.Equal("en", first.Content.Headers.ContentLanguage.Single());

        using var again = await SendAsync(HttpMethod.Get, "/Trailhead/strings/en", tweak: r => r.Headers.TryAddWithoutValidation("If-None-Match", etag));
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);

        using var other = await SendAsync(HttpMethod.Get, "/Trailhead/strings/en", tweak: r => r.Headers.TryAddWithoutValidation("If-None-Match", "\"some-other-tag\""));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("e")]
    [InlineData("en_us")]
    [InlineData("a1")]
    [InlineData("en-")]
    [InlineData("en--us")]
    [InlineData("pt-BR-")]
    [InlineData("toolong")]
    [InlineData("en-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("en%20us")]
    public async Task A_language_that_is_not_a_language_tag_is_a_400(string lang)
    {
        using var response = await GetAsync("/Trailhead/strings/" + lang);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", Code(response));
    }

    [Fact]
    public async Task Languages_that_do_not_exist_do_not_make_the_server_build_tables()
    {
        var merged = typeof(Strings).GetField("_merged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(merged);
        (await GetAsync("/Trailhead/strings/en")).Dispose();
        var baseline = ((ICollection)merged!.GetValue(UiText.Table)!).Count;

        foreach (var lang in new[] { "aa", "ab", "xx", "zz", "xx-yy", "qq-zz-1234", "de", "de-de", "fr-ca", "ja", "ko", "pt-br", "ru", "abc", "zzz", "EN-us" })
        {
            using var response = await GetAsync("/Trailhead/strings/" + lang);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.True(baseline >= 1);
        Assert.Equal(baseline, ((ICollection)merged.GetValue(UiText.Table)!).Count);
    }
}

/// <summary>A second language in the tables, to prove error bodies and answers are translated from Accept-Language.</summary>
[Collection(RegistryCollection.Name)]
public sealed class TranslationTests : ApiTestBase
{
    internal override Strings? StringsOverride => Strings.FromJson(new Dictionary<string, string>
    {
        ["en"] = JsonSerializer.Serialize(UiText.Table.TableFor("en")),
        ["xx"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["error.dynu_key_invalid"] = "XX invalid key",
            ["label.tooShort"] = "XX at least {min}",
        }),
    });

    private async Task<string> InvalidKeyBodyAsync(string? acceptLanguage)
    {
        using var response = await SendAsync(HttpMethod.Post, "/Trailhead/dynu-key", body: "{\"Key\":\"\"}", tweak: r =>
        {
            if (acceptLanguage is not null)
            {
                r.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            }
        });
        Assert.Equal("dynu_key_invalid", Code(response));
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("xx", "XX invalid key")]
    [InlineData("xx-YY", "XX invalid key")]
    [InlineData("de, xx;q=0.5", "XX invalid key")]
    [InlineData("en;q=0.2, xx;q=0.9", "XX invalid key")]
    [InlineData("xx;q=0, en", "That doesn't look like a Dynu key. Copy it again from API Credentials.")]
    [InlineData("de", "That doesn't look like a Dynu key. Copy it again from API Credentials.")]
    [InlineData("*", "That doesn't look like a Dynu key. Copy it again from API Credentials.")]
    [InlineData(null, "That doesn't look like a Dynu key. Copy it again from API Credentials.")]
    public async Task An_error_body_is_in_the_language_Accept_Language_asks_for_or_English(string? header, string expected)
    {
        Assert.Equal(expected, await InvalidKeyBodyAsync(header));
    }

    [Fact]
    public async Task A_name_answer_message_is_translated_and_a_key_missing_from_the_translation_falls_back_to_English()
    {
        SeedKey();

        using var response = await SendAsync(HttpMethod.Post, "/Trailhead/name/check", body: "{\"Label\":\"ab\",\"Parent\":\"freeddns.org\"}", tweak: r => r.Headers.TryAddWithoutValidation("Accept-Language", "xx"));

        using var json = await JsonAsync(response);
        Assert.Equal("XX at least 3", json.RootElement.GetProperty("Message").GetString());
        Assert.Equal("TooShort", json.RootElement.GetProperty("Problem").GetString());
    }

    [Fact]
    public async Task The_strings_route_merges_the_language_over_English()
    {
        using var response = await GetAsync("/Trailhead/strings/xx-yy");

        using var json = await JsonAsync(response);
        Assert.Equal("XX invalid key", json.RootElement.GetProperty("error.dynu_key_invalid").GetString());
        Assert.Equal(UiText.English("name.kind.taken"), json.RootElement.GetProperty("name.kind.taken").GetString());
        Assert.Equal("xx", response.Content.Headers.ContentLanguage.Single());
    }
}
