using System.Globalization;
using System.Xml.Linq;

namespace Jibo.Cloud.Tests.Application;

internal static class NativeTtsPromptAssertions
{
    // BEnch/usr/local/var/www/ttsservice/index.html documents pitch mult/add,
    // duration set/stretch, and forbids nesting either tag inside itself.
    // jibo-tts-service.json configures maxChars=1000; the native library also
    // contains a 500-character guard. Reserve space for BEam's serialization.
    internal static XElement AssertCompatible(string prompt)
    {
        Assert.InRange(prompt.Length, 1, 400);
        var tree = XElement.Parse(prompt);
        Assert.Equal("speak", tree.Name.LocalName);
        Assert.False(string.IsNullOrWhiteSpace(tree.Value));
        foreach (var tag in tree.Descendants())
        {
            Assert.Contains(tag.Name.LocalName, new[] { "pitch", "duration" });
            Assert.DoesNotContain(tag.Ancestors(), ancestor => ancestor.Name == tag.Name);
            var attribute = Assert.Single(tag.Attributes());
            Assert.Equal(tag.Name.LocalName == "pitch" ? "mult" : "set", attribute.Name.LocalName);
            var value = double.Parse(attribute.Value, CultureInfo.InvariantCulture);
            Assert.True(double.IsFinite(value) && value > 0);
        }
        return tree;
    }
}
