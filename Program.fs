open System
open System.IO
open System.Net
open System.Text
open System.Net.Sockets

let printableByte b =
    if b >= ' 'B && b <= '~'B
    then b else '.'B

let dump (bs : byte[]) offset =
    let out = "00000000                                                  |                |\n"B
    let ind1 = 7
    let ind2 = 9
    let indL = 59

    let conv i v =
        let t = byte(i>>>(v*4) &&& 0x0Fu) + '0'B
        if t > 0x39uy then t+7uy else t
    let conv' i v = 
        let t = byte(i>>>(v*4) &&& 0x0Fuy) + '0'B
        if t > 0x39uy then t+7uy else t

    for x in [0..ind1] do out.[ind1-x] <- conv (uint32 offset) x
    let min = Math.Min(8, bs.Length - offset)
    for x in [0..min-1] do 
        out.[ind2+x*3+0] <- conv' bs.[x+offset] 1
        out.[ind2+x*3+1] <- conv' bs.[x+offset] 0
        out.[indL+x]     <- printableByte bs.[x+offset]
    let min = Math.Min(16, bs.Length - offset)
    for x in [8..min-1] do 
        out.[ind2+1+x*3+0] <- conv' bs.[x+offset] 1
        out.[ind2+1+x*3+1] <- conv' bs.[x+offset] 0
        out.[indL+x]       <- printableByte bs.[x+offset]

    out

let dump' (bs : byte[]) = seq {
    for offset in [0..16..bs.Length-1] do
    yield dump bs offset
    }

let toHumanReadable bs =
    Array.concat (dump' bs)

type NS = Net.Sockets.NetworkStream

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

let async_read (s : NS) data = async {
    try
        let! data = s.AsyncRead(data, 0, data.Length)
        if data <> 0 then return Some data
        else              return None
    with
        | :? ObjectDisposedException -> return None
    }

let pass_through (rstm : NS) (wstm : NS) (log : Logger) (bin_log: Logger) = 
    let data : byte[] = Array.zeroCreate (64*1024)
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
        
        let log      = Logger(log_name, toHumanReadable)
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
            process_connection c n host port |> Async.Start
            loop (n + 1)
            ()
        loop 1
        
    listen_forever()

main()

