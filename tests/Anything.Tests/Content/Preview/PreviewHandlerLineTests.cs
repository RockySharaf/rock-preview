using Anything.Preview;

namespace Anything.Tests.Content.Preview;

/// <summary>
/// <see cref="PreviewPane.HandlerFailedLine"/>: the line in the tooltip and on the info card when the file's own previewer
/// failed. It names the previewer once, not twice.
/// </summary>
public sealed class PreviewHandlerLineTests
{
    [Fact]
    public void A_reason_that_starts_with_the_previewer_does_not_say_it_twice()
        => Assert.Equal("Microsoft Word previewer: could not be started (0x80040154)",
            PreviewPane.HandlerFailedLine("Microsoft Word previewer", "the previewer could not be started (0x80040154)"));

    [Fact]
    public void Any_other_reason_is_kept_as_it_is()
        => Assert.Equal("The file's own previewer: failed", PreviewPane.HandlerFailedLine("The file's own previewer", "failed"));
}
