using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed partial class History : IAsyncDisposable
{
    private sealed record WriteRequest(Action<SqliteConnection> Action,TaskCompletionSource Completion);
    private readonly Channel<WriteRequest> _writeQueue=Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(1024){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly object _writerStart=new();
    private Task? _writerTask;
    private Exception? _writeFailure;
    private bool _writerClosed;
    [ThreadStatic] private static History? _executingHistory;
    private SqliteConnection? _batchConnection;
    public event Action<Exception>? WriteFailed;
    public Exception? WriteFailure=>Volatile.Read(ref _writeFailure);
    public long CommittedBatches {get;private set;}
    public Task RecordAsync(Action action)=>QueueWriteAsync(_=>action());
    private void Write(Action<SqliteConnection> action)
    {
        if(_executingHistory==this){action(_batchConnection!);return;}
        QueueWriteAsync(action).GetAwaiter().GetResult();
    }
    private async Task QueueWriteAsync(Action<SqliteConnection> action)
    {
        lock(_writerStart)
        {
            if(_writeFailure is not null)throw new IOException("历史记录写入已中断。",_writeFailure);
            if(_writerClosed)throw new ObjectDisposedException(nameof(History));
            _writerTask??=Task.Run(WriteLoopAsync);
        }
        var request=new WriteRequest(action,new(TaskCreationOptions.RunContinuationsAsynchronously));
        await _writeQueue.Writer.WriteAsync(request).ConfigureAwait(false);
        await request.Completion.Task.ConfigureAwait(false);
    }
    private async Task WriteLoopAsync()
    {
        var batch=new List<WriteRequest>(128);
        try
        {
            while(await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                if(_writeQueue.Reader.TryRead(out var first))batch.Add(first);
                // Let ready producers join the transaction without delaying isolated writes.
                await Task.Yield();
                while(batch.Count<128&&_writeQueue.Reader.TryRead(out var item))batch.Add(item);
                using(var db=Open())
                using(var tx=db.BeginTransaction())
                {
                    _batchConnection=db;_executingHistory=this;
                    try{foreach(var item in batch)item.Action(db);tx.Commit();CommittedBatches++;}
                    finally{_executingHistory=null;_batchConnection=null;}
                }
                foreach(var item in batch)item.Completion.TrySetResult();
            }
        }
        catch(Exception ex)
        {
            Volatile.Write(ref _writeFailure,ex);_writeQueue.Writer.TryComplete(ex);
            foreach(var item in batch)item.Completion.TrySetException(ex);
            while(_writeQueue.Reader.TryRead(out var item))item.Completion.TrySetException(ex);
            try{WriteFailed?.Invoke(ex);}catch{/* Failure reporting must not hide the original disk failure. */}
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task? writer;lock(_writerStart){_writerClosed=true;_writeQueue.Writer.TryComplete();writer=_writerTask;}
        if(writer is not null)await writer.ConfigureAwait(false);
    }
}
