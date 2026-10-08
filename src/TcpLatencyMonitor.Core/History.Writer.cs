using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed partial class History : IAsyncDisposable
{
    private sealed record WriteRequest(Action<SqliteConnection> Action,TaskCompletionSource Completion,bool Optional=false);
    private readonly Channel<WriteRequest> _writeQueue=Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(1024){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly object _writerStart=new();
    private Task? _writerTask;
    private Exception? _writeFailure;
    private bool _writerClosed;
    [ThreadStatic] private static History? _executingHistory;
    private SqliteConnection? _batchConnection;
    private long _optionalSavepoint;
    public event Action<Exception>? WriteFailed;
    /// <summary>A rejected optional record, not a failure of the measurement writer.</summary>
    public event Action<Exception>? OptionalWriteFailed;
    public event Action<string?,string?>? RouteSourceCommitted;
    private readonly List<(string? Target,string? Route)> _routeSourceChanges=new();
    private void RouteSourceChanged(string? target,string? route=null)=>_routeSourceChanges.Add((target,route));
    public Exception? WriteFailure=>Volatile.Read(ref _writeFailure);
    public long CommittedBatches {get;private set;}
    public Task RecordAsync(Action action)=>QueueWriteAsync(_=>action());
    private void Write(Action<SqliteConnection> action)
    {
        if(_executingHistory==this){action(_batchConnection!);return;}
        QueueWriteAsync(action).GetAwaiter().GetResult();
    }
    // A constraint or malformed derived value is recoverable. Storage, locking,
    // corruption, cancellation and programming failures are deliberately not hidden.
    private static bool IsOptionalDataFailure(Exception error)=>error is JsonException or ArgumentException or FormatException or InvalidDataException||
        error is SqliteException {SqliteErrorCode:19};
    private bool TryWriteOptional(Action<SqliteConnection> action)
    {
        try
        {
            if(_executingHistory==this)
            {
                // RecordAsync can nest a derived write inside a measurement action.
                // Keep even that caller's earlier measurements when the derived row fails.
                using var cmd=_batchConnection!.CreateCommand();
                string savepoint="optional_"+(++_optionalSavepoint).ToString(System.Globalization.CultureInfo.InvariantCulture);
                int changes=_routeSourceChanges.Count;
                cmd.CommandText="SAVEPOINT "+savepoint;cmd.ExecuteNonQuery();
                try{action(_batchConnection);cmd.CommandText="RELEASE "+savepoint;cmd.ExecuteNonQuery();}
                catch(Exception error) when(IsOptionalDataFailure(error))
                {
                    cmd.CommandText="ROLLBACK TO "+savepoint+"; RELEASE "+savepoint;cmd.ExecuteNonQuery();
                    if(_routeSourceChanges.Count>changes)_routeSourceChanges.RemoveRange(changes,_routeSourceChanges.Count-changes);
                    throw;
                }
            }
            else QueueWriteAsync(action,optional:true).GetAwaiter().GetResult();
            return true;
        }
        catch(Exception error) when(IsOptionalDataFailure(error))
        {
            if(OptionalWriteFailed is {} handlers)foreach(Action<Exception> handler in handlers.GetInvocationList())
                try{handler(error);}catch{ /* A diagnostic listener cannot fail sampling. */ }
            return false;
        }
    }
    private async Task QueueWriteAsync(Action<SqliteConnection> action,bool optional=false)
    {
        lock(_writerStart)
        {
            if(_writeFailure is not null)throw new IOException("历史记录写入已中断。",_writeFailure);
            if(_writerClosed)throw new ObjectDisposedException(nameof(History));
            _writerTask??=Task.Run(WriteLoopAsync);
        }
        var request=new WriteRequest(action,new(TaskCreationOptions.RunContinuationsAsynchronously),optional);
        await _writeQueue.Writer.WriteAsync(request).ConfigureAwait(false);
        await request.Completion.Task.ConfigureAwait(false);
    }
    private void CommitWrites(List<WriteRequest> batch,int start,int count)
    {
        _routeSourceChanges.Clear();
        using(var db=Open())
        using(var tx=db.BeginTransaction())
        {
            _batchConnection=db;_executingHistory=this;
            try{for(int i=start;i<start+count;i++)batch[i].Action(db);tx.Commit();CommittedBatches++;}
            finally{_executingHistory=null;_batchConnection=null;}
        }
        // Optional derived consumers run only after commit, outside the write
        // transaction. Their exceptions cannot close the sampling queue.
        foreach(var change in _routeSourceChanges.Distinct())
            if(RouteSourceCommitted is {} handlers)foreach(Action<string?,string?> handler in handlers.GetInvocationList())
                try{handler(change.Target,change.Route);}catch{ /* Derived analysis is isolated. */ }
        for(int i=start;i<start+count;i++)batch[i].Completion.TrySetResult();
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
                for(int start=0;start<batch.Count;)
                {
                    if(batch[start].Optional)
                    {
                        // Optional records never share the sampling transaction. Disposing
                        // this transaction rolls back a rejected record before continuing.
                        try{CommitWrites(batch,start,1);}
                        catch(Exception error) when(IsOptionalDataFailure(error)){batch[start].Completion.TrySetException(error);}
                        start++;
                    }
                    else
                    {
                        int end=start+1;while(end<batch.Count&&!batch[end].Optional)end++;
                        CommitWrites(batch,start,end-start);start=end;
                    }
                }
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
