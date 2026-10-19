using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Liro.Application.Alerts;
using Liro.Infrastructure.Alerts;
using Liro.RegressionTests;
namespace Liro.Discord.Alerts.RegressionTests;
internal static class BandwidthApiTests
{
 static readonly DateTimeOffset Now=new(2026,10,19,12,0,0,TimeSpan.Zero);
 static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 sealed class Stub(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
 public static void Register(RegressionSuite suite)
 {
  suite.AsyncTest("Webshare_UsesActualBytes_CurrentPlan_AndCycleDates",async()=>{
   var paths=new List<string>();
   using var http=new HttpClient(new Stub(r=>{
    Check(r.Method==HttpMethod.Get && r.RequestUri!.Host=="proxy.webshare.io","Unexpected request destination");
    Check(r.Headers.Authorization?.Scheme=="Token" && r.Headers.Authorization.Parameter=="test-key","Missing API key");
    paths.Add(r.RequestUri!.PathAndQuery);
    return paths.Count switch{
     1=>Json("""{"id":1,"plan":2,"start_date":"2026-10-17T00:00:00Z","end_date":"2026-11-16T00:00:00Z"}"""),
     2=>Json("""{"id":2,"status":"active","bandwidth_limit":250}"""),
     _=>Json("""{"bandwidth_total":175000000000,"bandwidth_projected":9999999999999}""")};
   }));
   var usage=await new WebshareUsageClient(http).ReadAsync("test-key",Now,default);
   Check(usage.Percent==70 && usage.LimitBytes==250_000_000_000m,"Uses projection or incorrect units");
   Check(paths.Count==3 && paths[1].EndsWith("plan/2/") && paths[2].Contains("plan_id=2") && Uri.UnescapeDataString(paths[2]).Contains("timestamp__lte=2026-10-19T12:00:00"),"Missing plan or current-time bound");
  });
  foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.InternalServerError})
   suite.AsyncTest("Webshare_FailurePreservesStatusAndRetryAfter_"+(int)status,async()=>{
    var calls=0;using var http=new HttpClient(new Stub(r=>{calls++;var x=new HttpResponseMessage(status){Content=new StringContent("private-response")};x.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));return x;}));
    WebshareApiException? error=null;try{await new WebshareUsageClient(http).ReadAsync("private-key",Now,default);}catch(WebshareApiException e){error=e;}
    Check(error?.Code=="WebshareHTTP"+(int)status && error.RetryAfter==TimeSpan.FromMinutes(30) && calls==1,"Failure became zero usage or was retried immediately");
    Check(!error!.ToString().Contains("private"),"Error exposed secrets");
   });
  suite.AsyncTest("Webshare_MismatchedPlanNeverReadsUsage",async()=>{
   var calls=0;using var http=new HttpClient(new Stub(r=>++calls==1?Json("""{"id":1,"plan":2,"start_date":"2026-10-17T00:00:00Z","end_date":"2026-11-16T00:00:00Z"}"""):Json("""{"id":3,"status":"active","bandwidth_limit":250}""")));
   var rejected=false;try{await new WebshareUsageClient(http).ReadAsync("key",Now,default);}catch(InvalidDataException){rejected=true;}
   Check(rejected && calls==2,"Combines different plans");
  });
 }
}

