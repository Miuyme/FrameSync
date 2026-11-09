public sealed class DelayedNetworkMessage<T>
{
    public T Data;
    public double DueTime;

    public DelayedNetworkMessage(T data, double dueTime)
    {
        Data = data;
        DueTime = dueTime;
    }
}