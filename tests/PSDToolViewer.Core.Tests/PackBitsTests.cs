using PSDToolViewer.Core.Formats.Psd;

namespace PSDToolViewer.Core.Tests;

public class PackBitsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1000)]
    public void RoundTrip(int length)
    {
        var random = new Random(length);
        foreach (var source in new[]
        {
            Enumerable.Range(0, length).Select(_ => (byte)random.Next(256)).ToArray(),
            Enumerable.Repeat((byte)7, length).ToArray(),
            Enumerable.Range(0, length).Select(i => (byte)(i / 5 % 3)).ToArray(),
            Enumerable.Range(0, length).Select(i => (byte)(i % 2)).ToArray(),
        })
        {
            using var ms = new MemoryStream();
            int written = PackBits.Encode(source, ms);
            Assert.Equal(ms.Length, written);
            var decoded = new byte[length];
            PackBits.Decode(ms.ToArray(), decoded);
            Assert.Equal(source, decoded);
        }
    }
}
