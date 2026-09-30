namespace BaiZe.Mediator;

/// <summary>
/// 请求/响应客户端。由 IScopeMediator.CreateRequestClient&lt;TRequest&gt;() 创建，
/// 镜像 MassTransit 的 IRequestClient：请求与响应在同一进程内按引用传递，无序列化、无 broker。
/// </summary>
public interface IRequestClient<TRequest> where TRequest : class
{
    /// <summary>
    /// 发送请求并等待响应。请求消息按引用直达对应 Consumer；
    /// Consumer 调用 context.RespondAsync 后返回包含响应的 <see cref="Response{TResponse}"/>。
    /// </summary>
    Task<Response<TResponse>> GetResponse<TResponse>(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>包装一次请求/响应结果。镜像 MassTransit 的 Response&lt;T&gt;。</summary>
public sealed class Response<TResponse>
{
    public TResponse Message { get; }
    public Response(TResponse message) => Message = message;
}
