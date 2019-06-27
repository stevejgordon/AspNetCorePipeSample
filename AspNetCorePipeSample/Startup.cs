using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace AspNetCorePipeSample
{
    public class Startup
    {
        public static ReadOnlySpan<byte> PathProperty => new[] { (byte)'p', (byte)'a', (byte)'t', (byte)'h' };

        public static ReadOnlySpan<byte> UrlPrefix => new[] { (byte)'h', (byte)'t', (byte)'t', (byte)'p', (byte)'s', (byte)':', (byte)'/', (byte)'/', (byte)'w', (byte)'w', (byte)'w', (byte)'.',
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', (byte)'.', (byte)'c', (byte)'o', (byte)'m' };

        public void ConfigureServices(IServiceCollection services)
        {
        }
        
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/", async context =>
                {
                    var cancellationToken = context.RequestAborted;

                    var model = await ReadModelAsync(context.Request.BodyReader, cancellationToken);
                    
                    context.Response.ContentType = "text/plain";
                    context.Response.Headers[HeaderNames.CacheControl] = "no-cache";

                    await context.Response.StartAsync(cancellationToken);

                    await WriteUrlAsync(context.Response.BodyWriter, model, cancellationToken);
                });
            });
        }

        private static async Task<InputModel> ReadModelAsync(PipeReader reader, CancellationToken cancellationToken)
        {
            InputModel model = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                var readResult = await reader.ReadAsync(cancellationToken);
                var buffer = readResult.Buffer;

                var position = buffer.PositionOf((byte)'}');

                if (position != null)
                {
                    if (buffer.IsSingleSegment)
                    {
                        model = JsonSerializer.Parse<InputModel>(buffer.FirstSpan, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    else
                    {
                        using var document = JsonDocument.Parse(buffer);

                        if (document.RootElement.TryGetProperty(PathProperty, out var pathProperty)
                            && pathProperty.Type == JsonValueType.String)
                        {
                            model = new InputModel
                            {
                                Path = pathProperty.GetString()
                            };
                        }
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted) break;
            }

            return model;
        }

        private static async Task WriteUrlAsync(PipeWriter pipeWriter, InputModel model, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Cannot write message after request is complete.");
            }

            var bytesWritten = BuildUrl(pipeWriter.GetMemory().Span, model);

            pipeWriter.Advance(bytesWritten);

            await pipeWriter.FlushAsync(cancellationToken);
        }

        private static int BuildUrl(Span<byte> response, InputModel model)
        {
            UrlPrefix.CopyTo(response);

            var position = UrlPrefix.Length;

            if (string.IsNullOrEmpty(model?.Path))
                return position; // nothing more to append

            response[position++] = (byte)'/';

            var bytesWritten = Encoding.UTF8.GetBytes(model.Path, response.Slice(position));
            position += bytesWritten;

            return position;
        }

        private class InputModel
        {
            public string Path { get; set; }
        }
    }
}
