using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FWAStatsJobCore
{
    public class Program
    {
        public static HttpClient client = new HttpClient() { Timeout = new TimeSpan(0, 5, 0) };
        public static Logger logger = new LogFactory().GetLogger("Program");

        public static string FWAStatsURL { get; set; }

        public static int ThreadCount { get; set; } = Environment.ProcessorCount;

        private static Dictionary<Type, DataContractJsonSerializer> serializers = new Dictionary<Type, DataContractJsonSerializer>();

        private static DataContractJsonSerializer GetSerializer(Type type)
        {
            if (serializers.ContainsKey(type))
                return serializers[type];
            var serializer = new DataContractJsonSerializer(type);
            serializers.Add(type, serializer);
            return serializer;
        }

        private async static Task<string> Request(string page)
        {
            var url = string.Format("{0}/{1}", FWAStatsURL, page);
            return await client.GetStringAsync(url);
        }

        private async static Task<T> Request<T>(string page)
        {
            var pageData = await Request(page);
            var serializer = GetSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(pageData)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        public static async Task<int> UpdateClans()
        {
            var failures = 0;

            bool indexFound = false;
            int indexFailures = 0;
            while (indexFound == false && indexFailures < 5)
            {
                logger.Info("Update clans started, connecting to {0}", FWAStatsURL);
                try
                {
                    var data = Request("home/ping");
                    indexFound = true;
                }
                catch (Exception e)
                {
                    indexFailures++;
                    logger.Error(e.ToString);
                }
            }

            var index = await Request<UpdateIndexView>("Update/GetTasks");

            if (index != null)
            {
                foreach (var e in index.errors)
                {
                    logger.Error(e);
                }

                var queue = new BlockingCollection<UpdateTask>();
                var failQueue = new BlockingCollection<UpdateTask>();

                foreach (var task in index.tasks)
                {
                    queue.Add(task);
                }
                queue.CompleteAdding();

                var taskCount = ThreadCount;

                if (ServicePointManager.DefaultConnectionLimit < taskCount)
                    ServicePointManager.DefaultConnectionLimit = taskCount;

                logger.Info("Processing {0} clans with {1} threads", index.tasks.Count, taskCount);

                var tasks = new List<Task<int>>();
                for (var i = 0; i < taskCount; i++)
                {
                    tasks.Add(Task.Run(() => PerformClanUpdate(queue, failQueue)));
                    Thread.Sleep(2000); //First requests may timeout if all threads are added at once
                }

                foreach (var task in tasks)
                {
                    failures += task.Result; //Waiting thread to finish
                }

                failQueue.CompleteAdding();

                if (failQueue.Count > 0)
                {
                    logger.Info("Retrying {0} failed updates", failQueue.Count);
                    failures += await PerformClanUpdate(failQueue); //Cleanup in main thread
                }

                bool statisticsDone = false;
                int statisticsFailures = 0;
                while (statisticsDone == false && statisticsFailures < 5)
                {
                    try
                    {
                        logger.Info("Updating statistics...");
                        var finish = await Request<TaskStatus>("Update/UpdateFinished/");
                        if (finish != null)
                        {
                            logger.Info("{0}: {1}", finish.message, finish.status);
                            statisticsDone = finish.status;
                        }
                    }
                    catch (Exception e)
                    {
                        statisticsFailures++;
                        logger.Error(e.ToString);
                    }
                }


            }
            logger.Info("Run finished, {0} failures", failures);
            return failures;
        }

        public static async Task<int> UpdatePlayers()
        {
            var failures = 0;

            logger.Info("Run started, connecting to {0}", FWAStatsURL);
            var index = await Request<List<string>>("Update/PlayerBatch");

            if (index != null && index.Count > 0)
            {
                var queue = new BlockingCollection<string>();
                var failedQueue = new BlockingCollection<string>();

                foreach (var tag in index)
                {
                    queue.Add(tag);
                }
                queue.CompleteAdding();

                if (ServicePointManager.DefaultConnectionLimit < ThreadCount)
                    ServicePointManager.DefaultConnectionLimit = ThreadCount;

                logger.Info("Processing {0} players with {1} threads", index.Count, ThreadCount);

                var tasks = new List<Task<int>>();
                for (var i = 0; i < ThreadCount; i++)
                {
                    tasks.Add(Task.Run(() => PerformPlayerUpdate(queue)));
                    Thread.Sleep(2000);
                }

                foreach (var task in tasks)
                {
                    failures += task.Result;
                }
            }
            logger.Info("Player update finished, {0} failures", failures);
            return failures;
        }

        public static async Task<int> PerformClanUpdate(BlockingCollection<UpdateTask> queue, BlockingCollection<UpdateTask> failQueue = null)
        {
            int failures = 0;
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out UpdateTask task))
                {
                    try
                    {
                        var status = await Request<TaskStatus>(string.Format("Update/UpdateTask/{0}", task.id));
                        if (status != null)
                        {
                            logger.Info("{0}: {1}: {2}: {3}", Thread.CurrentThread.ManagedThreadId, queue.Count, status.message, status.status);
                            if (!status.status)
                                failures++;
                            if (status.message.Contains("API Error ProtocolError"))
                            {
                                logger.Error("ProtocolError detected, emptying queue");
                                while (queue.TryTake(out UpdateTask task2)) { }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error("{0}: {1}: {2} ({3})", Thread.CurrentThread.ManagedThreadId, task.clanName, e.Message, task.id);
                        if (e.Message.Contains("API Error ProtocolError"))
                        {
                            logger.Error("ProtocolError detected, emptying queue");
                            while (queue.TryTake(out UpdateTask task2)) { }
                        }
                        else
                        {
                            failures++;
                            if (failQueue != null)
                            {
                                if (failQueue.TryAdd(task))
                                    failures--;
                            }
                        }
                    }
                }
            }
            return failures;
        }

        public static async Task<int> PerformPlayerUpdate(BlockingCollection<string> queue)
        {
            int failures = 0;
            while (!queue.IsCompleted)
            {

                if (queue.TryTake(out string tag))
                {
                    try
                    {
                        var status = await Request<TaskStatus>(string.Format("Update/UpdatePlayerTask/{0}", Uri.EscapeDataString(tag)));
                        if (status != null)
                        {
                            logger.Info("{0}: {1}: {2}: {3}", Thread.CurrentThread.ManagedThreadId, queue.Count, status.message, status.status);
                            if (!status.status)
                                failures++;
                            if (status.message.Contains("API Error ProtocolError"))
                            {
                                logger.Error("ProtocolError detected, emptying queue");
                                while (queue.TryTake(out string tag2)) { }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Info("{0}: {1}: {2}", Thread.CurrentThread.ManagedThreadId, tag, e.Message);
                        if (e.Message.Contains("API Error ProtocolError"))
                        {
                            logger.Error("ProtocolError detected, emptying queue");
                            while (queue.TryTake(out string tag2)) { }
                        }
                        else
                        {
                            failures++;
                        }
                    }
                }
            }
            return failures;
        }

        static async Task<int> MainAsync(string[] args)
        {
            try
            {
                bool readUrl = false;
                bool readThreads = false;
                foreach (var arg in args)
                {
                    if (readUrl)
                    {
                        FWAStatsURL = arg;
                        readUrl = false;
                    }
                    else if (readThreads)
                    {
                        ThreadCount = int.Parse(arg);
                        readThreads = false;
                    }
                    else if (arg == "-url")
                    {
                        readUrl = true;
                    }
                    else if (arg == "-threads")
                    {
                        readThreads = true;
                    }
                    else
                    {
                        logger.Error("Unknown parameter: {0}", arg);
                    }
                }
                int clanErrors = await UpdateClans();
                int playerErrors = await UpdatePlayers();
                logger.Info(string.Format("{0} clan update errors, {1} player update errors", clanErrors, playerErrors));
                return clanErrors + playerErrors;
            }
            catch(Exception e)
            {
                logger.Error(e.ToString());
            }

            return -1;
        }

        static int Main(string[] args)
        {
            var t = Task.Run(() => MainAsync(args));
            t.Wait();
            return t.Result;
        }
    }
}
