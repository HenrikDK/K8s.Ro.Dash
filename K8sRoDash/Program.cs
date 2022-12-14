using K8sRoDash.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseLamar((context, registry) =>
{
    registry.Scan(x =>
    {
        x.AssemblyContainingType<Program>();
        x.WithDefaultConventions();
        x.LookForRegistries();
    });
});

builder.WebHost
    .ConfigureKestrel(x => x.ListenAnyIP(8080))
    .ConfigureLogging((context, config) =>
    {
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .WriteTo.Console(new JsonFormatter(renderMessage: true))
            .CreateLogger();
        
        config.ClearProviders();
        config.AddSerilog(logger);
    });

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("K8sClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }
);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
});

var app = builder.Build();

if (!app.Environment.IsDevelopment() || Debugger.IsAttached)
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseWebSockets();
app.UseStaticFiles();
app.UseRouting();
app.UseProxies(proxies =>
{
    proxies.Map("k8s/{**rest}",
        proxy => proxy.UseHttp((context, args) =>
        {
            context.Request.Headers.Add("Authorization", $"Bearer {K8sClient.AccessToken}");
            var qs = context.Request.QueryString.Value;
            var url = context.Request.Path.ToString().Replace("/k8s/", "/");
            var server = K8sClient.Server;
            return $"{server}{url}{qs}";
        }, builder => builder.WithHttpClientName("K8sClient"))
        .UseWs((context, args) => 
        {
            context.Request.Headers.Add("Authorization", $"Bearer {K8sClient.AccessToken}");
            var qs = context.Request.QueryString.Value;
            var url = context.Request.Path.ToString().Replace("/k8s/", "/");
            var server = K8sClient.Server.Replace("https://", "wss://");
            return $"{server}{url}{qs}";
        }, options => options.WithBeforeConnect((context, wso) =>
            {
                context.Request.Headers.Remove("Origin");
                wso.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true; 
                return Task.CompletedTask;
            })
            .WithHandleFailure((context, e) =>
            {
                app.Logger.LogError(e, "Exception proxying websocket");
                context.Response.StatusCode = 502;
                return Task.CompletedTask;
            }).Build())
    );
});
app.UseAuthorization();
app.UseEndpoints(x =>
{
    x.MapRazorPages();
    x.MapMetrics();
    x.MapControllers();
});

app.Run();
