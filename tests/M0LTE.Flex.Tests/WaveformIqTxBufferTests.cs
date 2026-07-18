using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>The back-pressured IQ TX buffer behind <see cref="FlexWaveformIqOutput"/>: FIFO order,
/// zero-pad on starve, and blocking <see cref="WaveformIqTxBuffer.Write"/> when full. No radio.</summary>
public sealed class WaveformIqTxBufferTests
{
    [Fact]
    public void Take_returns_written_iq_in_order_then_zero_pads_on_starve()
    {
        var buffer = new WaveformIqTxBuffer(capacityPairs: 16);
        buffer.Write([1f, 2f, 3f, 4f]); // 2 complex pairs

        var destination = new float[8]; // radio asks for 4 pairs
        buffer.TakePacket(destination);

        destination.Should().Equal(1f, 2f, 3f, 4f, 0f, 0f, 0f, 0f);
        buffer.SamplesStarved.Should().Be(2); // the 2 unfilled pairs
    }

    [Fact]
    public void Successive_takes_drain_in_fifo_order()
    {
        var buffer = new WaveformIqTxBuffer(capacityPairs: 16);
        buffer.Write([1f, 2f, 3f, 4f, 5f, 6f]); // 3 pairs

        var first = new float[4];
        buffer.TakePacket(first);
        var second = new float[4];
        buffer.TakePacket(second);

        first.Should().Equal(1f, 2f, 3f, 4f);
        second.Should().Equal(5f, 6f, 0f, 0f);
    }

    [Fact]
    public async Task Write_blocks_while_full_until_a_take_frees_space()
    {
        var buffer = new WaveformIqTxBuffer(capacityPairs: 2); // holds 2 pairs (4 floats)
        buffer.Write([1f, 2f, 3f, 4f]);                        // now full

        Task writer = Task.Run(() => buffer.Write([5f, 6f]));  // blocks — no space
        Task first = await Task.WhenAny(writer, Task.Delay(200));
        first.Should().NotBeSameAs(writer, "Write must block while the ring is full");

        buffer.TakePacket(new float[2]);                       // free one pair
        Task done = await Task.WhenAny(writer, Task.Delay(2000));
        done.Should().BeSameAs(writer);
        await writer;
    }

    [Fact]
    public async Task Complete_unblocks_a_full_writer_and_drops_the_remainder()
    {
        var buffer = new WaveformIqTxBuffer(capacityPairs: 1); // 1 pair
        buffer.Write([1f, 2f]);                                // full

        Task writer = Task.Run(() => buffer.Write([3f, 4f]));  // blocks
        buffer.Complete();

        Task done = await Task.WhenAny(writer, Task.Delay(2000));
        done.Should().BeSameAs(writer, "Complete must release a blocked producer");
        await writer;
    }

    [Fact]
    public void WaitDrained_returns_true_once_empty_and_false_on_timeout()
    {
        var buffer = new WaveformIqTxBuffer(capacityPairs: 8);
        buffer.Write([1f, 2f, 3f, 4f]);

        buffer.WaitDrained(TimeSpan.FromMilliseconds(100)).Should().BeFalse("still has undrained IQ");
        buffer.TakePacket(new float[4]);
        buffer.WaitDrained(TimeSpan.FromSeconds(1)).Should().BeTrue();
    }
}
