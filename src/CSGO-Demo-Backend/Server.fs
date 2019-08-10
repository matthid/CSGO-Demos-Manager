open System
open System.IO
open System.Threading.Tasks

open Microsoft.AspNetCore
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

open FSharp.Control.Tasks.V2
open Giraffe
open Shared
open Services.Interfaces
open Services.Concrete.Excel
open Services.Concrete
open System.Collections.Generic
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.Http
open System.Threading

module LogEvents =
    let MissingReplayFolder = EventId 20001
    let BoilerExitCode = EventId 20002
    let ErrorMMDownload = EventId 20003

type INotificationService =
    abstract SendNotification : notification:Notification -> Task

type IMyDemoService =
    abstract member Cache : Async<IReadOnlyCollection<Core.Models.Demo>> with get
    abstract member StartMMDownload : CancellationToken -> unit
    
type MyDemoService(logger:ILogger<MyDemoService>, cache : ICacheService, steam:ISteamService, notifications:INotificationService, demoService:IDemosService) =
    do Core.Logger.CoreInstance <- logger
    let demos = cache.GetDemoListAsync()
    do demoService.DownloadFolderPath <-
        let csGoPath = Core.AppSettings.GetCsgoPath()
        let demoFolder = csGoPath + string Path.DirectorySeparatorChar + "replays"
        if Directory.Exists (demoFolder) then
            demoFolder
        else
            logger.LogWarning(LogEvents.MissingReplayFolder, "Replays folder '{folderName}' doesn't exit", demoFolder)
            demoService.DownloadFolderPath
    let sendErr msg = 
        notifications.SendNotification(Notification.Error msg)
    let send msg = 
        notifications.SendNotification(Notification.Hint msg)
    member x.ProcessDemosDownloaded (ct:CancellationToken) =
        task {
            let! demos = demoService.GetDemoListUrl()
            if demos.Count > 0 then
                for kv, i in demos |> Seq.mapi (fun i kv -> kv, i) do
                    let demoName, demoUrl = kv.Key, kv.Value
                    do! send (sprintf "Downloading '%s' (%d/%d)" demoName (i+1) demos.Count)
                    let! ok = demoService.DownloadDemo(demoUrl, demoName)
                    do! send (sprintf "Extracting '%s' (%d/%d)" demoName (i+1) demos.Count)
                    let! ok = demoService.DecompressDemoArchive(demoName)
                    ()
                do! send "All done."                
            else 
                do! send "No newer demos found"

        }
    interface IMyDemoService with
        member x.Cache
            with get () =
                async {
                    let! de = demos |> Async.AwaitTask
                    return de :> IReadOnlyCollection<_>
                }

        member x.StartMMDownload (ct:CancellationToken) =
            task {
                if not (Directory.Exists demoService.DownloadFolderPath) then
                    do! sendErr (sprintf "Download folder '%s' not found" demoService.DownloadFolderPath)
                else
                try
                    let! result = steam.GenerateMatchListFile(ct)
                    logger.LogInformation(LogEvents.BoilerExitCode, "Boiler.exe result {exitCode}", result)
                    // See DemoListViewModel.HandleBoilerResult
                    match result with
                    | 1 -> do! sendErr "BoilerNotFound"
                    | 2 -> do! sendErr "DialogBoilerIncorrect"
                    | -1 -> do! sendErr "Invalid arguments"
                    | -2 -> do! sendErr "DialogRestartSteam"
                    | -3 | -4 -> do! sendErr "DialogSteamNotRunningOrNotLoggedIn"
                    | -5 | -6 | -7 -> do! sendErr "DialogErrorWhileRetrievingMatchesData"
                    | -8 -> do! sendErr "DialogNoNewerDemo"
                    | 0 -> do! x.ProcessDemosDownloaded ct
                    | _ -> do! sendErr (sprintf "Unknown boiler exit code '%d'" result)
                with e ->
                    logger.LogError(LogEvents.ErrorMMDownload, e, "Error in StartMMDownload")
                    do! sendErr <| e.ToString()
            }
            |> ignore        

let tryGetEnv = System.Environment.GetEnvironmentVariable >> function null | "" -> None | x -> Some x

let publicPath =
    let t1 = Path.GetFullPath "../Client/deploy"
    let t2 = Path.GetFullPath "./Client"
    let t3 = Path.GetFullPath "./Client/deploy"
    let all = [ t1; t2; t3 ]
    all
    |> Seq.tryFind (fun p ->
        File.Exists (Path.Combine(p, "index.html")))
    |> Option.defaultWith (fun () ->
        failwithf "Could not find client directory, tried %A" all)    
let port =
    "SERVER_PORT"
    |> tryGetEnv |> Option.map uint16 |> Option.defaultValue 8085us

module Seq =
    let tryTake (n : int) (s : _ seq) =
        seq {
            use e = s.GetEnumerator ()
            let mutable i = 0
            while e.MoveNext () && i < n do
                i <- i + 1
                yield e.Current
        }
    let trySkip (n : int) (s : _ seq) =
        seq {
            use e = s.GetEnumerator ()
            let mutable i = 0
            let mutable dataAvailable = e.MoveNext ()
            while dataAvailable && i < n do
                dataAvailable <- e.MoveNext ()
                i <- i + 1
            if dataAvailable then
                yield e.Current
                while e.MoveNext () do
                    yield e.Current
        }



let webApp =
    choose [
        route "/api/downloadMM" >=>
            fun next ctx ->
                task {
                    let r = { Status = "Ok" }
                    //let notification = ctx.GetService<INotificationService>()
                    //let demoService = ctx.GetService<IDemosService>()
                    //do! notification.SendNotification (Notification.Hint (sprintf "downloadPath: %s" demoService.DownloadFolderPath))
                    let demoService = ctx.GetService<IMyDemoService>()
                    demoService.StartMMDownload CancellationToken.None
                    return! json r next ctx
                }
        route "/api/demos" >=>
            fun next ctx ->
                task {
                    let startItem =
                        let t = ctx.Request.Query.["startItem"]
                        if t.Count > 0 then Int32.Parse t.[0] else 0
                    let maxItems =
                        let t = ctx.Request.Query.["maxItems"]
                        if t.Count > 0 then Int32.Parse t.[0] else 50
                    let sortBy =
                        let t = ctx.Request.Query.["sortBy"]
                        if t.Count > 0 then t.[0] else "Date"
                    let desc =
                        let t = ctx.Request.Query.["desc"]
                        if t.Count > 0 then bool.Parse t.[0] else true

                    let cache = ctx.GetService<IMyDemoService>()
                    let! demos = cache.Cache
                    let sortFunc proj s = s |> (if desc then Seq.sortByDescending proj else Seq.sortBy proj)
                    let sortedDemos =
                        match sortBy with
                        | "Date" -> demos |> sortFunc (fun d -> d.Date)
                        | "Name" ->  demos |> sortFunc (fun d -> d.Name)
                        | "Hostname" -> demos |> sortFunc (fun d -> d.Hostname)
                        | "Duration" -> demos |> sortFunc (fun d -> d.Duration)
                        | _ -> failwithf "unknown sort column '%s'" sortBy
                    printfn "Have %d demos, skipping %d and taking %d" demos.Count startItem maxItems
                    let demoData =
                        { Demos = sortedDemos |> Seq.trySkip startItem |> Seq.tryTake maxItems |> Seq.map ConvertToShared.ofDemo |> List.ofSeq
                          Pages = demos.Count / maxItems + (if demos.Count % maxItems = 0 then 0 else 1) }
                    return! json demoData next ctx
                }
    ]

type NotificationHub () =
    inherit Hub()


type NotificationService (context: IHubContext<NotificationHub>) =
    interface INotificationService with
        member x.SendNotification notification =
            let s = Thoth.Json.Net.Encode.Auto.toString(0, notification)
            context.Clients.All.SendAsync("Notification", s)

let configureApp (app : IApplicationBuilder) =
    app.UseDefaultFiles()
       .UseStaticFiles() |> ignore<IApplicationBuilder>
    app.UseSignalR(fun routes ->
        routes.MapHub<NotificationHub>(PathString "/socket/notifications"))
        |> ignore<IApplicationBuilder>
    app
       .UseGiraffe webApp

let configureServices (services : IServiceCollection) =

    // Create run time view services and models
    services.AddSingleton<IDemosService, DemosService>() |> ignore<IServiceCollection>
    services.AddSingleton<ISteamService, SteamService>() |> ignore<IServiceCollection>
    services.AddSingleton<ICacheService, CacheService>() |> ignore<IServiceCollection>
    services.AddSingleton<ExcelService, ExcelService>() |> ignore<IServiceCollection>
    services.AddSingleton<IFlashbangService, FlashbangService>() |> ignore<IServiceCollection>
    services.AddSingleton<IKillService, KillService>() |> ignore<IServiceCollection>
    services.AddSingleton<IRoundService, RoundService>() |> ignore<IServiceCollection>
    services.AddSingleton<IPlayerService, PlayerService>() |> ignore<IServiceCollection>
    services.AddSingleton<IDamageService, DamageService>() |> ignore<IServiceCollection>
    services.AddSingleton<IStuffService, StuffService>()|> ignore<IServiceCollection>
    services.AddSingleton<IAccountStatsService, AccountStatsService>() |> ignore<IServiceCollection>
    services.AddSingleton<IMyDemoService, MyDemoService>() |> ignore<IServiceCollection>
    services.AddSingleton<INotificationService, NotificationService>() |> ignore<IServiceCollection>
    //services.AddSingleton<IMapService, MapService>();
    //services.AddSingleton<IDialogService, DialogService>();

    services.AddGiraffe() |> ignore<IServiceCollection>
    
    services.AddSignalR() |> ignore<ISignalRServerBuilder>
    services.AddLogging(fun configure -> configure.AddConsole() |> ignore<ILoggingBuilder>) |> ignore<IServiceCollection>
    services.AddSingleton<Giraffe.Serialization.Json.IJsonSerializer>(Thoth.Json.Giraffe.ThothSerializer()) |> ignore<IServiceCollection>

let host =
    WebHost
        .CreateDefaultBuilder()
        .UseWebRoot(publicPath)
        .UseContentRoot(publicPath)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .UseUrls("http://0.0.0.0:" + port.ToString() + "/")
        .Build()

host.StartAsync().GetAwaiter().GetResult()
// Start to build demo cache
host.Services.GetService<IMyDemoService>() |> ignore<IMyDemoService>

printfn "Started server, write 'exit<Enter>' to stop the server"
let mutable hasExited = false
while not hasExited do
    let currentCommand = System.Console.ReadLine()
    if currentCommand = "exit" then
        hasExited <- true
    else    
        printfn "Unknown command '%s'" currentCommand

host.StopAsync().GetAwaiter().GetResult()
printfn "Proper Backend Shutdown finished"
