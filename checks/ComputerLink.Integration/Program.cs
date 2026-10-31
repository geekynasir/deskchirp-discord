using System.Diagnostics;
using DeveloperPresence.Core.Computers;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
class LocalSettings : ISettingsService {
 public PresenceSettings SettingsValue = new();
 public PresenceSettings Settings => SettingsValue;
 public event Action<PresenceSettings>? SettingsChanged;
 public Task LoadSettingsAsync()=>Task.CompletedTask;
 public Task SaveSettingsAsync()=>Task.CompletedTask;
 public Task UpdateSettingsAsync(Action<PresenceSettings> change) {change(Settings);SettingsChanged?.Invoke(Settings);return Task.CompletedTask;}
}
class Input : IInputAgeReader {
 readonly Stopwatch watch = Stopwatch.StartNew();
 double initial;
 public Input(double age=0){initial=age;}
 public double? ReadSeconds()=>initial+watch.Elapsed.TotalSeconds;
 public void UseNow(){initial=0;watch.Restart();}
}
class Program {
 static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static async Task Main(){
 var address=Environment.GetEnvironmentVariable("TEST_LINK_ADDRESS")!;
 var inputA=new Input();var inputB=new Input(600);
 var a=new ComputerLinkService(new LocalSettings(),new InMemoryCredentialStore(),inputA);
 var b=new ComputerLinkService(new LocalSettings(),new InMemoryCredentialStore(),inputB);
 await a.LinkAsync(address,Environment.GetEnvironmentVariable("TEST_LINK_CODE")!,"Test Windows");
 Check(a.HasSavedLink,"First enrollment failed");
 a.UpdateContext(true,"424242424242424242");await a.PollOnceAsync();
 Check(a.CanPublish,"First computer should be selected");
 await a.CreateCodeAsync();await b.LinkAsync(address,a.PairingCode,"Test Mac");
 Check(b.HasSavedLink,"Second enrollment failed");
 b.UpdateContext(true,"424242424242424242");await b.PollOnceAsync();
 Check(!b.CanPublish&&a.CanPublish,"Idle computer must not win");
 await Task.Delay(3000);inputB.UseNow();await b.PollOnceAsync();
 await Task.Delay(2100);await b.PollOnceAsync();
 Check(!b.CanPublish,"New computer must wait for old permission expiry");
 await a.PollOnceAsync();Check(!a.CanPublish,"Old computer must stop");
 await Task.Delay(12000);await b.PollOnceAsync();await a.PollOnceAsync();
 Check(b.CanPublish&&!a.CanPublish,"Handoff should select only second computer");
 Console.WriteLine("PASS: two native C# clients enrolled, idle peer stayed quiet, and ownership switched after expiry over real HTTP");
 }
}