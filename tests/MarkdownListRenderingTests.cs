using System;
using YAOLlm;
using Xunit;

namespace YAOLlm.Tests;

/// <summary>
/// Markdig (UseAdvancedExtensions) parses single-letter and roman-numeral
/// list markers as ordered lists, emitting the marker letter as an ordinal
/// start plus a type attribute: "F." becomes &lt;ol type="A" start="6"&gt;.
/// The UI's decimal list-style override would then display the marker as
/// "6." instead of "F." (2026-09-25 incident: a model's one-letter "F."
/// reply showed up as a stray uncopyable "6." in the chat). The ui/index.html
/// CSS maps each type attribute back to its marker style; these tests lock
/// in the Markdig emission shape that fix depends on.
/// </summary>
public class MarkdownListRenderingTests
{
    [Theory]
    [InlineData("F.", "<ol type=\"A\" start=\"6\">")]
    [InlineData("f.", "<ol type=\"a\" start=\"6\">")]
    [InlineData("A.", "<ol type=\"A\">")]
    [InlineData("I.", "<ol type=\"I\">")]
    [InlineData("i.", "<ol type=\"i\">")]
    public void SingleLetterMarker_BecomesTypedListWithOrdinalStart(string markdown, string expectedHtml)
    {
        Assert.Contains(expectedHtml, MarkdownHelper.ToHtml(markdown));
    }

    [Fact]
    public void DigitLists_HaveNoTypeAttribute_DecimalCssStaysCorrect()
    {
        var html = MarkdownHelper.ToHtml("1. one\n2. two");

        Assert.Contains("<ol>", html);
        Assert.DoesNotContain("type=", html);
    }

    [Fact]
    public void LetterMarkerWithFollowingText_KeepsTextInsideTheListItem()
    {
        var html = MarkdownHelper.ToHtml("F. text right here");

        Assert.Contains("<ol type=\"A\" start=\"6\">", html);
        Assert.Contains("<li>text right here</li>", html);
    }
}
