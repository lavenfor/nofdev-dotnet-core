using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Nofdev.Core;
using ServiceContext = Nofdev.Core.SOA.ServiceContext;
using Nofdev.Core.SOA;

namespace Nofdev.Server.Controllers
{
    [Route("facade")]
    [Route("service")]
    [Route("micro")]
    public class RpcApiController : Controller
    {
        private readonly ILogger<RpcApiController> _logger;

        public bool EnableStackTrace { get; set; } = false;

        public RpcApiController(ILogger<RpcApiController> logger)
        {
            _logger = logger;
        }

        [Route("[action]/{packageName}/{interfaceName}/{methodName}")]
        public async Task<JsonResult> Json(string packageName, string interfaceName,
            string methodName, [FromBody] string @params)
        {

            var serviceLayer = HttpContext.GetRouteValue("serviceLayer").ToString();//todo:
        
            var httpJsonResponse = new HttpJsonResponse<dynamic>();

            httpJsonResponse.callId = RefreshCallId();

         
            ExceptionMessage exceptionMessage = null;
            try
            {
                _logger.LogDebug($"ask instance for interface {interfaceName}");

                var serviceType = GetServiceType(packageName, interfaceName, serviceLayer);
                var service = GetServiceInstance(serviceType);
                _logger.LogDebug(
                    $"JSON facade call(callId:{httpJsonResponse.callId}): {interfaceName}.{methodName}{@params}");
                await Invoke(serviceType, service, methodName, @params).ContinueWith(it => httpJsonResponse.val = it.Result);
            }
            catch (AbstractBusinessException e)
            {
                _logger.LogInformation(
                        $"Business exception occured when calling JSON facade call(callId:{httpJsonResponse.callId}): {interfaceName}.{methodName}{@params}: {e.Message}");
                Response.StatusCode = 500;
                exceptionMessage = FormatException(e);
            }
            catch (Exception e)
            {
               _logger.LogError(
                        $"Unhandled exception occured when calling JSON facade call(callId:{httpJsonResponse.callId}): {interfaceName}.{methodName}{@params}: {e.Message}");
                Response.StatusCode = 500;
                exceptionMessage = FormatException(e);
            }
            httpJsonResponse.err = exceptionMessage;
            return  new JsonResult(httpJsonResponse);
        }

        protected virtual  Type GetServiceType(string packageName, string interfaceName, string serviceLayer)
        {
            var key =
                $"{serviceLayer}.{packageName.Replace('-', '.')}.{interfaceName}.json"
                    .ToLower();


            if (!ServiceRegistry.Instance.ContainsKey(key))
                key =
                    $"{serviceLayer}.{packageName.Replace('-', '.')}.{interfaceName}{serviceLayer}.json"
                        .ToLower();

            if (!ServiceRegistry.Instance.ContainsKey(key))
            {
                throw new InvalidOperationException($"Can not find interface {interfaceName}.");
            }
            return ServiceRegistry.Instance[key]; 
        }

        protected virtual object GetServiceInstance(Type serviceType)
        {
            return this.HttpContext.RequestServices.GetService(serviceType);
        }


        protected async Task<object> Invoke(Type serviceType, object service, string methodName, string @params)
        {
            object val = null;

            var methods = serviceType.GetMethods();
            var method = methods.FirstOrDefault(m => string.Compare(m.Name, methodName, StringComparison.CurrentCultureIgnoreCase) == 0);
            if (method != null)
            {
                //async task
                if (method.ReturnType.GetTypeInfo().BaseType == typeof(Task))
                {
                    if (@params != null && "null" != @params)
                    {
                        var task =
                            (Task)
                                method.Invoke(service,
                                    (await DeserializeAsync(@params, method.GetParameters().Select(p => p.ParameterType).ToArray())).ToArray());
                        await task.ContinueWith(it =>
                        {
                            dynamic dtask = it;
                            val = dtask.Result;

                        });
                    }
                    else
                    {
                        var task = (Task)method.Invoke(service, null);
                        await task.ContinueWith(it =>
                        {
                            dynamic dtask = it;
                            val = dtask.Result;
                        });
                    }

                }
                else
                {
                    if (@params != null && "null" != @params)
                    {
                        val = method.Invoke(service,
                            Deserialize(@params, method.GetParameters().Select(p => p.ParameterType).ToArray())
                                .ToArray());
                    }
                    else
                    {
                        val = method.Invoke(service, null);
                    }
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Can not find method {methodName} of interface type {serviceType}.");
            }


            return val;
        }

        protected string RefreshCallId()
        {
            var parentCallId = ServiceContext.Current?.CallId.Parent;
            return !string.IsNullOrWhiteSpace(parentCallId) ? parentCallId :
                ServiceContext.Current?.CallId.Id;
        }


        private IEnumerable<object> Deserialize(string rawParams, Type[] paramTypes)
        {
            var array = JArray.Parse(rawParams);
            return array.Select((item, i) => item.ToObject(paramTypes[i]));
        }


        private async Task<IEnumerable<object>> DeserializeAsync(string rawParams, Type[] paramTypes)
        {
            var array = JArray.Parse(rawParams);
            return await Task.Run(() => array.Select((item, i) => item.ToObject(paramTypes[i])));
        }

        protected ExceptionMessage FormatException(Exception exception)
        {
            if (exception == null) return null;
            var exceptionMessage = new ExceptionMessage();
            exceptionMessage.Name = exception.GetType().Name;
            exceptionMessage.Msg = exception.Message;
            exceptionMessage.Cause = FormatException(exception.InnerException);
            if (EnableStackTrace)
            {
                exceptionMessage.Stack = exception.StackTrace;
            }
            return exceptionMessage;
        }


    }
}