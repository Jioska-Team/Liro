using System.Net;
using Liro.MarketWorker;
namespace Liro.RegressionTests;
internal static class MarketProxyFailureTests
{
 public static void Register(RegressionSuite suite)
 {
  suite.AsyncTest("Proxy402_PausesAccount_WithoutLeakingCredentials",async()=>{
   var pauses=new List<TimeSpan>();
   var error=await Check.ThrowsAsync<MarketHttpException>(()=>MarketProxyFailure.SendAsync(
    ()=>Task.FromException<HttpResponseMessage>(new HttpRequestException("proxy user:secret rejected",null,HttpStatusCode.PaymentRequired)),
    delay=>{pauses.Add(delay);return Task.CompletedTask;}));
   Check.That(error.Code=="ProxyHTTP402" && error.Limited,"No distingue el rechazo del proveedor");
   Check.That(pauses.Count==1 && pauses[0]==TimeSpan.FromMinutes(15) && error.Delay==pauses[0],"No pausa la cuenta durante 15 minutos");
   Check.That(!error.ToString().Contains("secret"),"Expone credenciales");
  });
  suite.AsyncTest("ProxySuccess_PreservesResponse_WithoutPause",async()=>{
   using var expected=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("original")};
   var pauses=0;
   var actual=await MarketProxyFailure.SendAsync(()=>Task.FromResult(expected),_=>{pauses++;return Task.CompletedTask;});
   Check.That(ReferenceEquals(actual,expected) && await actual.Content.ReadAsStringAsync()=="original" && pauses==0,"Altera respuesta o pausa un éxito");
  });
  suite.AsyncTest("ProxyOtherFailure_IsNotMisclassifiedAs402",async()=>{
   var expected=new HttpRequestException("other",null,HttpStatusCode.BadGateway);var pauses=0;
   var actual=await Check.ThrowsAsync<HttpRequestException>(()=>MarketProxyFailure.SendAsync(()=>Task.FromException<HttpResponseMessage>(expected),_=>{pauses++;return Task.CompletedTask;}));
   Check.That(ReferenceEquals(actual,expected) && pauses==0,"Oculta un error distinto");
  });
  suite.AsyncTest("Proxy402_CooldownStorageFailure_StopsProcessing",async()=>{
   var expected=new InvalidOperationException("storage");
   var actual=await Check.ThrowsAsync<InvalidOperationException>(()=>MarketProxyFailure.SendAsync(()=>Task.FromException<HttpResponseMessage>(new HttpRequestException("402",null,HttpStatusCode.PaymentRequired)),_=>Task.FromException(expected)));
   Check.That(ReferenceEquals(actual,expected),"Ignora fallo de persistencia de la pausa");
  });
 }
}
