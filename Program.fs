open System
open System.IO
open System.Net
open System.Text
open System.Net.Sockets

let toBytes (s : string) = Text.Encoding.ASCII.GetBytes s
let printableChar c =
    if c >= byte(' ') && c <= byte('~') 
    then char(c).ToString()
    else "."

let printableString (bs : byte[])  = 
    String.init bs.Length (fun i -> printableChar bs.[i])

let hexDump (v : byte[]) = seq {
    let len = 16

    let printChunk v (index : int) =
        let sb = StringBuilder()
        sb.AppendFormat("{0:X8} {1:s} {2:s} |{3:s}|\n",
            index,
            (BitConverter.ToString(v, index, len/2)),
            (BitConverter.ToString(v, index+len/2, len/2)),
            (printableString (v.[index..index+len-1]))
            ) |> ignore
            
        sb.ToString()
                         
    for chunk in [0 .. len .. v.Length-len] do
        yield printChunk v chunk |> toBytes 

    if v.Length%len <> 0 then
        let sb = StringBuilder()
        let lastIndex = v.Length/len*len
        let last = v.[lastIndex..]
        let lastString = String.init last.Length (fun i -> printableChar last.[i])
        let i1 = Math.Min(len/2-1, last.Length-1)
        let i2 = Math.Min(len-1, last.Length-1)

        sb.AppendFormat("{0:X8} {1,-23:s} {2,-23:s} |{3,-16:s}|\n", 
            lastIndex,
            (BitConverter.ToString last.[0..i1]),
            (BitConverter.ToString [| for x in len/2..i2 -> last.[x] |]),
            lastString) |> ignore
        yield sb.ToString() |> toBytes 
}

let toHumanReadable bs = 
    Array.concat (hexDump bs)

type S = Net.Sockets.NetworkStream

type loggerMsg = Data of byte[] | Done of AsyncReplyChannel<unit>
type Logger(logname : string, f) =
    let file = File.OpenWrite logname
    let innerLoop = MailboxProcessor.Start(fun inbox -> 
        let rec loop isGood = async {
            let! msg = inbox.Receive()
            match msg with
            | Data d -> if isGood then
                            let d' = f d 
                            do! file.AsyncWrite(d', 0, d'.Length)
                        return! loop isGood
            | Done r -> r.Reply()
            }
        try
            loop true
        with
            e ->
                stderr.Write e
                loop false
        )
        
    member this.Write x = innerLoop.Post (Data x)
    member this.Stop()  = 
        innerLoop.PostAndReply(fun replyChannel -> Done replyChannel)
        file.Close()

let async_read (s : S) data = async {
    try
        let! data = s.AsyncRead(data, 0, data.Length)
        if data <> 0 then return Some data
        else              return None
    with
        | :? ObjectDisposedException -> return None
    }

let pass_through (rstm : S) (wstm : S) (log : Logger) (bin_log: Logger) = 
    let data : byte[] = Array.zeroCreate (16*1024)
    let rec loop() = async {
        let! dataRead = async_read rstm data
        match dataRead with
        | None   -> printfn "Read stream closed"
                    rstm.Close()
                    wstm.Close()
        | Some read ->
            try
                do! wstm.AsyncWrite(data, 0, read)
                let arr = data.[0..read-1]
                log.Write arr
                bin_log.Write arr
                return! loop()
            with _ ->
                printfn "Write stream closed"
                rstm.Close()
                wstm.Close()
    }
    loop()

let process_connection (client : TcpClient) n host port = async {
        use c = client // to call Dispose() at very end
        use remote = new TcpClient(host, port)
        let local_info  = remote.Client.RemoteEndPoint.ToString().Replace(':', '-')
        let remote_info = remote.Client.LocalEndPoint.ToString().Replace(':', '-')
        
        let start_time = System.DateTime.Now
        let log_name      = sprintf "log-%s-%04d-%s-%s.log" (start_time.ToLongDateString()) n local_info remote_info
        let binr_log_name = sprintf "log-binary-%s-%04d-%s.log" (start_time.ToLongDateString()) n remote_info
        let binl_log_name = sprintf "log-binary-%s-%04d-%s.log" (start_time.ToLongDateString()) n local_info
        
        let log     = Logger(log_name, toHumanReadable)
        let binr_log = Logger (binr_log_name, id)
        let binl_log = Logger (binl_log_name, id)
        [pass_through (c.GetStream()) (remote.GetStream()) log binr_log;
         pass_through (remote.GetStream()) (c.GetStream()) log binl_log]
        |> Async.Parallel |> Async.RunSynchronously |> ignore
        
        let finish_time = System.DateTime.Now
        printfn "finish time=%A" ((finish_time-start_time).ToString())   
        [log; binr_log; binl_log] |> Seq.iter (fun x -> x.Stop())
        let stop_time = System.DateTime.Now
        printfn "stop time=%A" ((stop_time-finish_time).ToString())   
    }

let main() =
    let args = Environment.GetCommandLineArgs()
    if args.Length <> 4 then
        System.IO.Path.GetFileName args.[0]
        |> printfn "Usage: %s <host> <port> <listen_port>"
        Environment.Exit 1

    let host = args.[1]
    let port = Convert.ToInt32 args.[2]
    let listen_port = Convert.ToInt32 args.[3]

    printfn "Listening on port %d and forwarding data to %s:%d" listen_port host port
    let listen_forever() = 
        let localhost = [|127uy;0uy;0uy;1uy|]
        let s = new TcpListener(IPAddress localhost, listen_port)
        s.Start()
        let rec loop n = 
            let c = s.AcceptTcpClient()
            process_connection c n host port |> Async.StartImmediate
            loop (n + 1)
            ()
        loop 1
        
    listen_forever()

main()