using AllDebridDownloader.Helpers;

namespace AllDebridDownloader.Tests;

public sealed class PathHelperTests
{
    // -----------------------------------------------------------------------
    // Segment sanitizing
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("normal.mkv", "normal.mkv")]
    [InlineData("with space.mkv", "with space.mkv")]
    [InlineData("a:b.mkv", "a_b.mkv")]
    [InlineData("a*b?c.mkv", "a_b_c.mkv")]
    [InlineData("a<b>c|d.mkv", "a_b_c_d.mkv")]
    [InlineData("a\"b.mkv", "a_b.mkv")]
    [InlineData("a/b.mkv", "a_b.mkv")]
    [InlineData("a\\b.mkv", "a_b.mkv")]
    public void Invalid_characters_are_replaced(string input, string expected)
        => Assert.Equal(expected, PathHelper.SanitizeSegment(input));

    [Fact]
    public void Runs_of_invalid_characters_collapse_to_one_underscore()
        => Assert.Equal("a_b.mkv", PathHelper.SanitizeSegment("a***???b.mkv"));

    [Fact]
    public void Control_characters_are_stripped()
        => Assert.Equal("a_b.mkv", PathHelper.SanitizeSegment("a\u0001\u0002b.mkv"));

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("LPT1.mkv", "LPT1_.mkv")]
    [InlineData("COM9", "COM9_")]
    [InlineData("AUX", "AUX_")]
    [InlineData("PRN.dat", "PRN_.dat")]
    public void Reserved_device_names_are_suffixed(string input, string expected)
        => Assert.Equal(expected, PathHelper.SanitizeSegment(input));

    [Fact]
    public void Names_that_merely_contain_a_device_name_are_left_alone()
    {
        Assert.Equal("CONCERT.mkv", PathHelper.SanitizeSegment("CONCERT.mkv"));
        Assert.Equal("nullable.cs", PathHelper.SanitizeSegment("nullable.cs"));
    }

    [Theory]
    [InlineData("trailing...", "trailing")]
    [InlineData("trailing   ", "trailing")]
    [InlineData("  leading", "leading")]
    [InlineData("mixed. . .", "mixed")]
    public void Trailing_dots_and_spaces_are_trimmed(string input, string expected)
        => Assert.Equal(expected, PathHelper.SanitizeSegment(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("???")]
    [InlineData("***")]
    [InlineData("<>|")]
    public void Empty_traversal_or_all_invalid_segments_become_a_placeholder(string? input)
        => Assert.Equal("unnamed", PathHelper.SanitizeSegment(input));

    // -----------------------------------------------------------------------
    // Relative paths
    // -----------------------------------------------------------------------

    [Fact]
    public void Relative_path_joins_sanitized_segments()
    {
        var result = PathHelper.SanitizeRelativePath(["Season 1", "S01E01.mkv"]);
        Assert.Equal(Path.Combine("Season 1", "S01E01.mkv"), result);
    }

    [Fact]
    public void Traversal_segments_are_dropped_from_a_relative_path()
    {
        var result = PathHelper.SanitizeRelativePath(["..", "..", "evil.exe"]);
        Assert.Equal("evil.exe", result);
    }

    [Fact]
    public void Separators_inside_one_segment_are_split_and_vetted()
    {
        var result = PathHelper.SanitizeRelativePath(["a/../../b/c.mkv"]);
        Assert.Equal(Path.Combine("a", "b", "c.mkv"), result);
    }

    [Fact]
    public void Drive_specifications_are_dropped()
    {
        var result = PathHelper.SanitizeRelativePath(["C:", "Windows", "System32", "evil.dll"]);
        Assert.Equal(Path.Combine("Windows", "System32", "evil.dll"), result);
    }

    [Fact]
    public void Empty_segment_list_becomes_the_placeholder()
        => Assert.Equal("unnamed", PathHelper.SanitizeRelativePath([]));

    // -----------------------------------------------------------------------
    // The traversal guard -- the security-critical assertion
    // -----------------------------------------------------------------------

    [Fact]
    public void Combine_safe_keeps_a_normal_path_inside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "adw-root");
        var result = PathHelper.CombineSafe(root, ["Show", "S01E01.mkv"]);

        Assert.True(PathHelper.IsInside(root, result));
        Assert.EndsWith(Path.Combine("Show", "S01E01.mkv"), result);
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("../../../../../../Windows/System32/evil.dll")]
    public void Traversal_attempts_are_neutralised_and_stay_inside_the_root(string attack)
    {
        var root = Path.Combine(Path.GetTempPath(), "adw-root");
        var result = PathHelper.CombineSafe(root, [attack]);

        // The segments are stripped, so the result is inside the root by construction.
        Assert.True(PathHelper.IsInside(root, result),
            "A traversal attempt escaped the root: " + result);
    }

    [Fact]
    public void An_absolute_remote_path_cannot_override_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "adw-root");
        var result = PathHelper.CombineSafe(root, ["C:\\Windows\\System32\\evil.dll"]);

        Assert.True(PathHelper.IsInside(root, result));
        Assert.DoesNotContain("System32\\evil.dll", result[..root.Length]);
    }

    [Fact]
    public void A_unc_path_cannot_override_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "adw-root");
        var result = PathHelper.CombineSafe(root, ["\\\\server\\share\\evil.exe"]);

        Assert.True(PathHelper.IsInside(root, result));
        Assert.StartsWith(Path.GetFullPath(root), result);
    }

    [Fact]
    public void Is_inside_accepts_the_root_itself_and_rejects_a_sibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "adw-root");

        Assert.True(PathHelper.IsInside(root, root));
        Assert.True(PathHelper.IsInside(root, Path.Combine(root, "child", "file.mkv")));

        // A sibling whose name merely starts with the root's name is not inside it.
        Assert.False(PathHelper.IsInside(root, root + "-other"));
        Assert.False(PathHelper.IsInside(root, Path.Combine(Path.GetTempPath(), "elsewhere")));
    }

    [Fact]
    public void Unsafe_path_exception_carries_the_details()
    {
        // Proves the guard actually throws, not just that sanitizing happens to help:
        // IsInside is the last line of defence and must be exercised directly.
        var root = Path.Combine(Path.GetTempPath(), "adw-root");
        var outside = Path.Combine(Path.GetTempPath(), "adw-root-other", "x.mkv");

        Assert.False(PathHelper.IsInside(root, outside));

        var ex = new UnsafePathException("../x.mkv", outside, root);
        Assert.Equal("../x.mkv", ex.RelativePath);
        Assert.Equal(outside, ex.ResolvedPath);
        Assert.Contains("outside the download folder", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Long paths
    // -----------------------------------------------------------------------

    [Fact]
    public void A_normal_path_is_not_shortened()
        => Assert.Null(PathHelper.ShortenIfTooLong(
            Path.Combine("C:\\Downloads", "Show", "S01E01.mkv")));

    [Fact]
    public void An_over_long_filename_is_trimmed_but_keeps_its_extension()
    {
        var longName = new string('a', 400) + ".mkv";
        var full = Path.Combine("C:\\Downloads", longName);

        var result = PathHelper.ShortenIfTooLong(full);

        Assert.NotNull(result);
        Assert.EndsWith(".mkv", result);
        Assert.True(Path.GetFileName(result).Length <= 255);
    }

    [Fact]
    public void An_over_long_total_path_is_trimmed()
    {
        // The directory leaves room for a short name, so the stem is trimmed to fit.
        var deep = Path.Combine("C:\\", string.Join("\\", Enumerable.Repeat("folder", 20)));
        var full = Path.Combine(deep, new string('b', 200) + ".mkv");

        var result = PathHelper.ShortenIfTooLong(full, maxTotalLength: 200);

        Assert.NotNull(result);
        Assert.True(result.Length <= 200, "still too long: " + result.Length);
        Assert.EndsWith(".mkv", result);
    }

    [Fact]
    public void A_directory_that_is_itself_too_long_is_reported_not_silently_accepted()
    {
        // No filename can rescue this, so the caller has to refuse the file rather than
        // proceed with a path Windows will reject at write time.
        var deep = Path.Combine("C:\\", string.Join("\\", Enumerable.Repeat("folder", 60)));
        var full = Path.Combine(deep, "x.mkv");

        Assert.Throws<PathTooLongException>(() =>
            PathHelper.ShortenIfTooLong(full, maxTotalLength: 100));
    }

    // -----------------------------------------------------------------------
    // Rename
    // -----------------------------------------------------------------------

    [Fact]
    public void Next_available_name_returns_the_original_when_free()
    {
        using var temp = new TempDir();
        var path = temp.File("movie.mkv");

        Assert.Equal(path, PathHelper.NextAvailableName(path));
    }

    [Fact]
    public void Next_available_name_appends_an_increasing_counter()
    {
        using var temp = new TempDir();
        var path = temp.File("movie.mkv");

        File.WriteAllText(path, "a");
        var second = PathHelper.NextAvailableName(path);
        Assert.Equal(temp.File("movie (2).mkv"), second);

        File.WriteAllText(second, "b");
        Assert.Equal(temp.File("movie (3).mkv"), PathHelper.NextAvailableName(path));
    }

    [Fact]
    public void Next_available_name_handles_a_file_with_no_extension()
    {
        using var temp = new TempDir();
        var path = temp.File("README");
        File.WriteAllText(path, "a");

        Assert.Equal(temp.File("README (2)"), PathHelper.NextAvailableName(path));
    }
}
