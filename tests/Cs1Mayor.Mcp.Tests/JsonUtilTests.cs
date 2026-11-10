using SkylinesAgentBridge.Support;

namespace Cs1Mayor.Mcp.Tests;

/// <summary>
/// Tests for the bridge's hand-rolled JSON reader.
///
/// The bridge cannot use a JSON library: the game's Mono runtime does not ship one, and
/// the mod must not depend on assemblies the game does not load. So <see cref="JsonUtil"/>
/// parses JSON with regular expressions. These tests pin down how it behaves, because a
/// subtle bug here shows up as a command that silently reads the wrong value.
/// </summary>
public class JsonUtilTests
{
    [Fact]
    public void GetString_reads_a_string_field()
    {
        Assert.Equal("Basic Road", JsonUtil.GetString("{\"roadPrefab\":\"Basic Road\"}", "roadPrefab", ""));
    }

    [Fact]
    public void GetString_returns_the_default_when_the_field_is_missing()
    {
        Assert.Equal("fallback", JsonUtil.GetString("{\"other\":1}", "roadPrefab", "fallback"));
    }

    [Fact]
    public void GetString_ignores_a_field_with_a_different_name_that_ends_the_same_way()
    {
        var json = "{\"notRoadPrefab\":\"Wrong\",\"roadPrefab\":\"Right\"}";

        Assert.Equal("Right", JsonUtil.GetString(json, "roadPrefab", ""));
    }

    [Fact]
    public void GetBool_reads_true_and_false()
    {
        Assert.True(JsonUtil.GetBool("{\"dryRun\":true}", "dryRun", false));
        Assert.False(JsonUtil.GetBool("{\"dryRun\":false}", "dryRun", true));
    }

    [Fact]
    public void GetBool_returns_the_default_when_the_field_is_missing()
    {
        Assert.True(JsonUtil.GetBool("{}", "dryRun", true));
    }

    [Fact]
    public void GetNumber_reads_a_number()
    {
        Assert.Equal(42.5f, JsonUtil.GetNumber("{\"radius\":42.5}", "radius", 0f));
    }

    [Fact]
    public void GetNumber_reads_a_negative_number()
    {
        Assert.Equal(-7f, JsonUtil.GetNumber("{\"offset\":-7}", "offset", 0f));
    }

    [Fact]
    public void GetNumber_returns_the_default_when_the_field_is_missing()
    {
        Assert.Equal(9f, JsonUtil.GetNumber("{}", "radius", 9f));
    }

    [Fact]
    public void GetPointNumber_reads_one_axis_of_a_point_object()
    {
        var json = "{\"start\":{\"x\":10,\"y\":20,\"z\":30}}";

        Assert.Equal(10f, JsonUtil.GetPointNumber(json, "start", "x", 0f));
        Assert.Equal(20f, JsonUtil.GetPointNumber(json, "start", "y", 0f));
        Assert.Equal(30f, JsonUtil.GetPointNumber(json, "start", "z", 0f));
    }

    [Fact]
    public void GetPointNumber_returns_the_default_when_the_point_is_missing()
    {
        Assert.Equal(5f, JsonUtil.GetPointNumber("{\"end\":{\"x\":1}}", "start", "x", 5f));
    }

    [Fact]
    public void GetObjectArray_returns_each_object_in_the_array()
    {
        var json = "{\"items\":[{\"a\":1},{\"b\":2}]}";

        var objects = JsonUtil.GetObjectArray(json, "items");

        Assert.Equal(2, objects.Count);
        Assert.Contains("\"a\":1", objects[0]);
        Assert.Contains("\"b\":2", objects[1]);
    }

    [Fact]
    public void GetObjectArray_returns_an_empty_list_when_the_array_is_missing()
    {
        Assert.Empty(JsonUtil.GetObjectArray("{\"other\":[]}", "items"));
    }

    [Fact]
    public void GetObjectArray_does_not_split_objects_that_contain_nested_braces()
    {
        var json = "{\"items\":[{\"point\":{\"x\":1,\"y\":2}}]}";

        var objects = JsonUtil.GetObjectArray(json, "items");

        Assert.Single(objects);
        Assert.Contains("\"x\":1", objects[0]);
    }

    [Fact]
    public void Escape_escapes_quotes_backslashes_and_newlines()
    {
        Assert.Equal("a\\\"b", JsonUtil.Escape("a\"b"));
        Assert.Equal("a\\\\b", JsonUtil.Escape("a\\b"));
        Assert.Equal("a\\nb", JsonUtil.Escape("a\nb"));
    }

    [Fact]
    public void Escape_returns_an_empty_string_for_null()
    {
        Assert.Equal("", JsonUtil.Escape(null));
    }

    [Fact]
    public void Bool_and_Number_render_json_primitives()
    {
        Assert.Equal("true", JsonUtil.Bool(true));
        Assert.Equal("false", JsonUtil.Bool(false));
        Assert.Equal("1.5", JsonUtil.Number(1.5f));
    }

    [Fact]
    public void CommandResult_Fail_carries_the_error_in_both_the_field_and_the_json()
    {
        var result = CommandResult.Fail("something broke");

        Assert.False(result.Ok);
        Assert.Equal("something broke", result.Error);
        Assert.Contains("something broke", result.Json);
        Assert.Contains("\"ok\":false", result.Json);
    }

    [Fact]
    public void CommandResult_FromJson_is_ok()
    {
        var result = CommandResult.FromJson("{\"ok\":true}");

        Assert.True(result.Ok);
        Assert.Equal("{\"ok\":true}", result.Json);
    }
}
