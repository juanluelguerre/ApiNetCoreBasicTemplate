﻿using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace ElGuerre.Items.Api.Infrastructure.Http
{
    public interface IHttpClient
    {
        Task<string> GetStringAsync(
            string uri,
            string authorizationToken = null,
            string authorizationMethod = "Bearer");

        Task<HttpResponseMessage> PostAsync<T>(string uri,
            T item,
            string authorizationToken = null,
            string requestId = null,
            string authorizationMethod = "Bearer");

        Task<HttpResponseMessage> PostFileAsync(
            string uri,
            byte[] file,
            string fileName,
            string authorizationToken = null,
            string requestId = null,
            string authorizationMethod = "Bearer");

        Task<HttpResponseMessage> PutAsync<T>(
            string uri,
            T item,
            string authorizationToken = null,
            string requestId = null,
            string authorizationMethod = "Bearer");

        Task<HttpResponseMessage> DeleteAsync(
            string uri,
            string authorizationToken = null,
            string requestId = null,
            string authorizationMethod = "Bearer");

    }
}