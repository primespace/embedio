﻿using EmbedIO.Constants;
using System.Threading.Tasks;
using EmbedIO.Modules;

namespace EmbedIO.Tests.TestObjects
{
    public class TestLocalSessionController : WebApiController
    {
        public const string DeleteSession = "deletesession";
        public const string PutData = "putdata";
        public const string GetData = "getdata";
        public const string GetCookie = "getcookie";

        public const string MyData = "MyData";
        public const string CookieName = "MyCookie";

        public TestLocalSessionController(IHttpContext context)
            : base(context)
        {
        }

        [WebApiHandler(HttpVerbs.Get, "/getcookie")]
        public Task<bool> GetCookieC()
        {
            var cookie = new System.Net.Cookie(CookieName, CookieName);
            Response.Cookies.Add(cookie);

            return Ok(Response.Cookies[CookieName]);
        }

        [WebApiHandler(HttpVerbs.Get, "/deletesession")]
        public Task<bool> DeleteSessionC()
        {
            HttpContext.Session.Delete();

            return Ok("Deleted");
        }

        [WebApiHandler(HttpVerbs.Get, "/putdata")]
        public Task<bool> PutDataSession()
        {
            HttpContext.Session["sessionData"] = MyData;

            return Ok(HttpContext.Session["sessionData"].ToString());
        }

        [WebApiHandler(HttpVerbs.Get, "/getdata")]
        public Task<bool> GetDataSession() =>
            Ok(HttpContext.Session["sessionData"]?.ToString() ?? string.Empty);

        [WebApiHandler(HttpVerbs.Get, "/geterror")]
        public bool GetError() => false;
    }
}