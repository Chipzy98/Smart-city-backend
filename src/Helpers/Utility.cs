using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartcityBackend.src.Helpers
{
    public static class Utility
    {
        
        public static string? GetAuthToken(HttpRequest request)
        {
            var authorizationHeader = request.Headers.Authorization.FirstOrDefault();
            var parts = authorizationHeader?.ToString().Split(null) ?? [];
            string? token = null;
            if (parts.Length == 2 && parts[0].ToLower().Equals("bearer"))
            {
                token = parts[1];
            }
            return token;
        }
    }
}
