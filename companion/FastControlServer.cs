using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TabletDeck;

// TLS plus bounded, big-endian length-prefixed JSON. Never accepts PC targets or admin routes.
internal sealed class FastControlServer(DataStore store,FastCommands commands,StateUpdates updates,X509Certificate2 certificate,int port) : IAsyncDisposable
{
    private readonly TcpListener listener=new(IPAddress.Any,port);
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<long,Task> clients=new();
    private readonly ConcurrentDictionary<TcpClient,string> principals=new();
    public int ConnectedDevices=>principals.Values.Distinct(StringComparer.Ordinal).Count();
    public bool Connected(string hash)=>principals.Values.Contains(hash,StringComparer.Ordinal);
    private long sequence;
    private Task? accepting;
    public void Start(){listener.Start(16);accepting=Accept();}
    private async Task Accept()
    {
        while(!stop.IsCancellationRequested) {
            TcpClient socket;
            try{socket=await listener.AcceptTcpClientAsync(stop.Token);}catch(OperationCanceledException){break;}
            if(clients.Count>=16){socket.Dispose();continue;}
            var id=Interlocked.Increment(ref sequence);
            var task=Task.Run(async()=>{try{await Serve(socket);}catch(Exception ex) when(ex is not OutOfMemoryException){/* A closed or invalid peer never stops other controls. */}finally{principals.TryRemove(socket,out _);socket.Dispose();}});
            clients[id]=task;_=task.ContinueWith(_=>clients.TryRemove(id,out var removed),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
    }
    internal static async Task<JsonDocument> Read(Stream stream,CancellationToken ct)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,ct);var length=BinaryPrimitives.ReadInt32BigEndian(header);
        if(length is <2 or >16384)throw new IOException("Control frame exceeds the limit.");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,ct);return JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});
    }
    private async Task Serve(TcpClient client)
    {
        client.NoDelay=true;client.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.KeepAlive,true);
        using var closed=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);handshake.CancelAfter(TimeSpan.FromSeconds(6));
        await using var stream=new SslStream(client.GetStream(),false);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},handshake.Token);
        using var authentication=await Read(stream,handshake.Token);
        var token=authentication.RootElement.GetProperty("token").GetString()??"";
        if(!store.Authenticate(token))return;
        var principal=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        principals[client]=principal;
        var ownedLease="";
        var ownedPenLease="";
        var seen=new HashSet<int>();var recent=new Queue<int>();
        using var sendGate=new SemaphoreSlim(1,1);using var requestSlots=new SemaphoreSlim(4,4);
        async Task Send(byte[] bytes) {
            if(bytes.Length>1024*1024)throw new IOException("Control response exceeds the limit.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(closed.Token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
            await sendGate.WaitAsync(deadline.Token);
            try{var frame=new byte[bytes.Length+4];BinaryPrimitives.WriteInt32BigEndian(frame,bytes.Length);bytes.CopyTo(frame,4);await stream.WriteAsync(frame,deadline.Token);await stream.FlushAsync(deadline.Token);}
            finally{sendGate.Release();}
        }
        Task Reply(object value)=>Send(JsonSerializer.SerializeToUtf8Bytes(value,DataStore.Json));
        await Reply(new{type="ready",protocol=1});
        var subscription=updates.Subscribe();
        var pushing=Task.Run(async()=>{
            try{await foreach(var bytes in subscription.Reader.ReadAllAsync(closed.Token)){if(!store.Authenticate(token)){closed.Cancel();client.Dispose();break;}await Send(bytes);}}
            catch(Exception ex) when(ex is not OutOfMemoryException){closed.Cancel();client.Dispose();}
        });
        var pending=new List<Task>();
        try {
            while(!closed.IsCancellationRequested) {
                using var idle=CancellationTokenSource.CreateLinkedTokenSource(closed.Token);idle.CancelAfter(TimeSpan.FromSeconds(25));
                using var document=await Read(stream,idle.Token);var request=document.RootElement;
                if(!store.Authenticate(token))break;
                var id=request.GetProperty("id").GetInt32();
                var path=request.GetProperty("path").GetString()??"";
                if(id<0||(id==0&&path!="/api/ping"))throw new ArgumentException("Invalid request ID.");
                if(id>0){if(!seen.Add(id)){await Reply(new{id,error="Duplicate request ID was refused; no action was repeated."});continue;}recent.Enqueue(id);if(recent.Count>4096)seen.Remove(recent.Dequeue());}
                if(!FastCommands.Supports(path)){await Reply(new{id,error="This request is not allowed on the fast control link."});continue;}
                if(!requestSlots.Wait(0)){await Reply(new{id,error="Control link is busy. Wait for the current action."});continue;}
                var body=request.TryGetProperty("body",out var value)?value.Clone():JsonSerializer.SerializeToElement(new{});
                pending.RemoveAll(task=>task.IsCompleted);
                pending.Add(Task.Run(async()=>{
                    try{closed.Token.ThrowIfCancellationRequested();if(!store.Authenticate(token))throw new InvalidOperationException("This tablet was revoked.");var result=await commands.Execute(path,body,principal);if(result is FastCommands.HoldLease lease)Volatile.Write(ref ownedLease,lease.Lease);if(result is WindowsPen.PenLease penLease)Volatile.Write(ref ownedPenLease,penLease.Lease);await Reply(new{id,result});}
                    catch(Exception ex) when(ex is not OutOfMemoryException){try{await Reply(new{id,error=ex.Message});}catch(Exception failure) when(failure is not OutOfMemoryException){closed.Cancel();client.Dispose();}}
                    finally{requestSlots.Release();}
                }));
            }
        }finally{
            closed.Cancel();updates.Unsubscribe(subscription.Id);holdsRelease();
            try{await pushing;}catch(OperationCanceledException){}
            // Finish in-flight commands before disposing their send/slot semaphores.
            try{await Task.WhenAll(pending);}catch(Exception ex) when(ex is not OutOfMemoryException){}
            holdsRelease();
        }
        void holdsRelease(){commands.Release(principal,Volatile.Read(ref ownedLease));commands.ReleasePen(principal,Volatile.Read(ref ownedPenLease));}
    }
    public void Revoke(string principal){foreach(var connection in principals)if(connection.Value==principal)connection.Key.Dispose();}
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();if(accepting is not null)try{await accepting;}catch(SocketException){}
        try{await Task.WhenAll(clients.Values);}catch(Exception ex) when(ex is not OutOfMemoryException){}stop.Dispose();
    }
}
