using System.Collections.Generic;

namespace FrameSync.Shared
{
    /// <summary>
    /// 客户端输入缓冲区
    /// </summary>
    public sealed class CommandBuffer
    {
        private readonly Queue<BufferedCommand> commands = new Queue<BufferedCommand>();
            

        private readonly int capacity;

        // 下一条允许加入的帧编号
        private int nextFrameToEnqueue;

        public int Count => commands.Count;

        public CommandBuffer(int firstFrame = 0, int capacity = 256)
        {
            if (firstFrame < 0)
                throw new System.ArgumentOutOfRangeException(
                    nameof(firstFrame));

            if (capacity <= 0)
                throw new System.ArgumentOutOfRangeException(
                    nameof(capacity));

            nextFrameToEnqueue = firstFrame;

            this.capacity = capacity;
        }

        /// <summary>
        /// 添加一条输入
        /// 必须严格按照帧编号递增
        /// </summary>
        public bool TryEnqueue(int frame,PlayerCommand command)

        {
            if (frame != nextFrameToEnqueue)
                return false;

            if (commands.Count >= capacity)
                return false;

            commands.Enqueue(new BufferedCommand(frame, command));
                

            nextFrameToEnqueue++;

            return true;
        }

        /// <summary>
        /// 取出下一条待发送输入
        /// </summary>
        public bool TryDequeue(out BufferedCommand command)

        {
            if (commands.Count == 0)
            {
                command = default(BufferedCommand);
                return false;
            }

            command = commands.Dequeue();

            return true;
        }

        /// <summary>
        /// 查看下一条输入，但不移除
        /// </summary>
        public bool TryPeek(out BufferedCommand command)
        {
            if (commands.Count == 0)
            {
                command = default(BufferedCommand);
                return false;
            }

            command = commands.Peek();

            return true;
        }
        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Clear()
        {
            commands.Clear();
        }
    }
}