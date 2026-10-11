namespace MaoX.Core;

/// <summary>手机上游戏输出写在日志文件里：读到文件末尾时等待新内容，游戏退出后读完剩余内容再结束。</summary>
public sealed class TailStream(Stream inner, Task exited) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            var done = exited.IsCompleted;
            var read = inner.Read(buffer, offset, count);
            if (read > 0 || done)
                return read;
            Thread.Sleep(200);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
