module Progam

open System
open System.Threading.Tasks
open ARMClient.Authentication.AADAuthentication
open ARMClient.Authentication.Contracts;
open ARMClient.Library
open Newtonsoft.Json
open Newtonsoft.Json.FSharp
open System.Net
open System.Net.Http

type ArmCollection<'a> = {
    value: 'a list option
}

type ArmResource<'a> = {
    id: string
    name: string
    kind: string
    location: string
    properties: 'a
}

type ArmSubscription = {
    id: string
    subscriptionId: string
    displayName: string
}

type ArmResourceGroup = {
    provisioningState: string
}

type ArmSite = {
    name: string
    enabledHostNames: string list
}

let fromJson<'a> (str: string): 'a = JsonConvert.DeserializeObject<'a> (str, new ListConverter(), new OptionConverter())

[<EntryPoint>]
let main argv = 
    if argv |> Array.length <> 1 || argv.[0].Equals ("help", StringComparison.OrdinalIgnoreCase) then
        printfn """Let's Encrypt Azure Client (1.0)

Usage:
    leaz example.com [Optional: -www]

Requirements:
    You HAVE to have openssl.exe on the PATH.
    Download: http://www.npcglib.org/~stathis/blog/precompiled-openssl/

Help:
    example.com has to be a domain that's already configured correctly to one of your azure websites.
    -www will include www.example.com in the certificate as well.

"""
    else
        ServicePointManager.DefaultConnectionLimit <- 500
        let authHelper = new PersistentAuthHelper ()
        authHelper.AzureEnvironments <- AzureEnvironments.Prod
        let azureClient = new AzureClient (3, authHelper)
        let wait (t: Task<'a>) = t |> Async.AwaitTask |> Async.RunSynchronously
        let parseTenant (str: string) = str.Split (':', '(') |> fun a -> a.[2].Trim ()

//        authHelper.AcquireTokens () |> wait
        authHelper.DumpTokenCache ()
        |> Seq.filter (fun l -> l.StartsWith ("User:"))
        |> Seq.map parseTenant
        |> Seq.filter (fun t -> t = "590d33be-69d3-46c7-b4f8-02a61d7658af")
        |> Seq.map (fun tenant ->
            async {
                authHelper.GetToken tenant |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                let! subscriptionsResponse = azureClient.HttpInvoke (HttpMethod.Get, new Uri ("https://management.azure.com/subscriptions?api-version=2014-04-01")) |> Async.AwaitTask
                subscriptionsResponse.EnsureSuccessStatusCode () |> ignore
                let! body = subscriptionsResponse.Content.ReadAsStringAsync () |> Async.AwaitTask
                let subscriptions = fromJson<ArmCollection<ArmSubscription>> body
                return
                    match subscriptions.value with
                    | None -> Seq.empty
                    | Some value ->
                        value
                        |> Seq.map (fun s -> s.subscriptionId)
                        |> Seq.map (fun subscription -> 
                            async {
                                let! resourceGroupsResponse = azureClient.HttpInvoke (HttpMethod.Get, new Uri (sprintf "https://management.azure.com/subscriptions/%s/resourceGroups?api-version=2014-04-01" subscription)) |> Async.AwaitTask
                                resourceGroupsResponse.EnsureSuccessStatusCode () |> ignore
                                let! body = resourceGroupsResponse.Content.ReadAsStringAsync () |> Async.AwaitTask
                                let resourceGroups = fromJson<ArmCollection<ArmResource<ArmResourceGroup>>> body
                                return
                                    match resourceGroups.value with
                                    | None -> Seq.empty
                                    | Some value ->
                                        value
                                        |> Seq.map (fun r -> r.name)
                                        |> Seq.map (fun resourceGroup ->
                                            async {
                                                let! webAppsResoponse = azureClient.HttpInvoke (HttpMethod.Get, new Uri (sprintf "https://management.azure.com/subscriptions/%s/resourceGroups/%s/providers/Microsoft.Web/sites?api-version=2015-08-01" subscription resourceGroup)) |> Async.AwaitTask
                                                webAppsResoponse.EnsureSuccessStatusCode () |> ignore
                                                let! body = webAppsResoponse.Content.ReadAsStringAsync () |> Async.AwaitTask
                                                return fromJson<ArmCollection<ArmResource<ArmSite>>> body
                                            } |> Async.RunSynchronously)
                            } |> Async.RunSynchronously)
            } |> Async.RunSynchronously)
        |> Seq.concat
        |> Seq.concat
        |> List.ofSeq
        |> List.map (fun s -> match s.value with | None -> List.empty | Some value -> value)
        |> List.concat
        |> List.filter (fun s -> s.name = argv.[0])
        |> Seq.iter (printfn "%A")
    0