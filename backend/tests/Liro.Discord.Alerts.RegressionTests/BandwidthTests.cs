using Liro.Application.Alerts;
using Liro.Infrastructure.Alerts;
using Liro.RegressionTests;
namespace Liro.Discord.Alerts.RegressionTests;
internal static class BandwidthTests
{
 static readonly DateTimeOffset Start=new(2026,10,17,0,0,0,TimeSpan.Zero);
 static BandwidthSnapshot Sample(decimal percent, string plan="1") => new("account",plan,Start,Start.AddDays(30),percent*10_000_000m,1_000_000_000m,Start.AddDays(1));
 static void Check(bool ok,string error){if(!ok)throw new Exception(error);}
 static async Task WithDirectory(Func<string,Task> action){var p=Path.Combine(AppContext.BaseDirectory,"liro-bandwidth-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);try{await action(p);}finally{Directory.Delete(p,true);}}
 public static void Register(RegressionSuite suite)
 {
  suite.AsyncTest("Bandwidth_ExactThresholds_AndRestartDeduplication",()=>WithDirectory(async dir=>{
   var sent=new List<PendingBandwidthAlert>(); Task Send(PendingBandwidthAlert a,CancellationToken ct){sent.Add(a);return Task.CompletedTask;}
   foreach(var value in new[]{69.99m,70m,70m,84.99m,85m,94.99m,95m,100m})
    await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123).ObserveAsync(Sample(value),Send,default);
   Check(sent.Select(a=>a.Threshold).SequenceEqual(new[]{70,85,95}),"Incorrect threshold or restart duplicate");
  }));
  suite.AsyncTest("Bandwidth_JumpTo95_EmitsOnlyHighest_AndNewCycleResets",()=>WithDirectory(async dir=>{
   var sent=new List<int>();Task Send(PendingBandwidthAlert a,CancellationToken ct){sent.Add(a.Threshold);return Task.CompletedTask;}
   var store=new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123);
   await store.ObserveAsync(Sample(99),Send,default);
   await store.ObserveAsync(Sample(75),Send,default);
   await store.ObserveAsync(Sample(75,"new-plan"),Send,default);
   await store.ObserveAsync(Sample(75,"new-plan") with {CycleStart=Start.AddDays(30),CycleEnd=Start.AddDays(60),ObservedAt=Start.AddDays(31)},Send,default);
   Check(sent.SequenceEqual(new[]{95,70,70}),"Repeats lower threshold or does not reset cycle");
  }));
  suite.AsyncTest("Bandwidth_PendingDelivery_RetriesSameIdentity_AfterRestart",()=>WithDirectory(async dir=>{
   PendingBandwidthAlert? first=null;var store=new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123);
   try{await store.ObserveAsync(Sample(85),(a,ct)=>{first=a;throw new IOException("ack lost");},default);}catch(IOException){}
   PendingBandwidthAlert? retried=null;
   await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123).ObserveAsync(Sample(85),(a,ct)=>{retried=a;return Task.CompletedTask;},default);
   Check(first!=null && retried?.Marker==first.Marker,"Pending notification lost or changed identity");
   var again=0;await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123).ObserveAsync(Sample(85),(a,ct)=>{again++;return Task.CompletedTask;},default);
   Check(again==0,"Resent acknowledged alert");
  }));
  suite.AsyncTest("Bandwidth_UnlimitedDoesNotDivideByZero_AndInvalidDataRejected",()=>WithDirectory(async dir=>{
   var sent=0;Task Send(PendingBandwidthAlert a,CancellationToken ct){sent++;return Task.CompletedTask;}
   var store=new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,123),123);
   await store.ObserveAsync(Sample(200) with {LimitBytes=0},Send,default);
   var rejected=false;try{await store.ObserveAsync(Sample(-1),Send,default);}catch(InvalidDataException){rejected=true;}
   Check(sent==0 && rejected,"Invalid or unlimited quota raised an alert");
  }));
  suite.AsyncTest("Bandwidth_CorruptStateDoesNotResendAndChannelIsolated",()=>WithDirectory(async dir=>{
   var sent=0;Task Send(PendingBandwidthAlert a,CancellationToken ct){sent++;return Task.CompletedTask;}
   await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,1),1).ObserveAsync(Sample(70),Send,default);
   await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,2),2).ObserveAsync(Sample(70),Send,default);
   Check(sent==2,"Channel state leaked");
   File.WriteAllText(Path.Combine(dir,"bandwidth-alerts-1.json"),"invalid");
   try{await new BandwidthAlertStore(new JsonBandwidthAlertStateStore(dir,1),1).ObserveAsync(Sample(70),Send,default);}catch(System.Text.Json.JsonException){}
   Check(sent==2,"Corrupt state resent messages");
  }));
 }
}

