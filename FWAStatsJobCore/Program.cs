using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FWAStatsJobCore
{
    public class Program
    {
        public static string FWAStatsURL { get; set; }

        private static Dictionary<Type, DataContractJsonSerializer> serializers = new Dictionary<Type, DataContractJsonSerializer>();

        private static DataContractJsonSerializer GetSerializer(Type type)
        {
            if (serializers.ContainsKey(type))
                return serializers[type];
            var serializer = new DataContractJsonSerializer(type);
            serializers.Add(type, serializer);
            return serializer;
        }

        private static string Request(string page)
        {
            var url = string.Format("{0}/{1}", FWAStatsURL, page);
            var request = (HttpWebRequest)HttpWebRequest.Create(url);
            request.Timeout = 5 * 60 * 1000; // 5 min
            request.ContentType = "application/json; charset=utf-8";
            var response = request.GetResponse();
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var data = reader.ReadToEnd();
                return data;
            }
        }

        private static T Request<T>(string page)
        {
            var pageData = Request(page);
            var serializer = GetSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(pageData)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        public static int UpdateClans()
        {
            var failures = 0;

            bool indexFound = false;
            int indexFailures = 0;
            while (indexFound == false && indexFailures < 5)
            {
                Log(string.Format("Update clans started, connecting to {0}", FWAStatsURL));
                try
                {
                    var data = Request("");
                    indexFound = true;
                }
                catch (Exception e)
                {
                    indexFailures++;
                    Log(e.Message);
                }
            }

            var index = Request<UpdateIndexView>("Update/GetTasks");

            if (index != null)
            {
                foreach (var e in index.errors)
                {
                    Log(e);
                }

                var queue = new BlockingCollection<UpdateTask>();
                var failQueue = new BlockingCollection<UpdateTask>();

                foreach (var task in index.tasks)
                {
                    queue.Add(task);
                }
                queue.CompleteAdding();

                var taskCount = Environment.ProcessorCount;

                if (ServicePointManager.DefaultConnectionLimit < taskCount)
                    ServicePointManager.DefaultConnectionLimit = taskCount;

                Log(string.Format("Processing {0} clans with {1} threads", index.tasks.Count, taskCount));

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
                    Log(string.Format("Retrying {0} failed updates", failQueue.Count));
                    failures += PerformClanUpdate(failQueue); //Cleanup in main thread
                }

                bool statisticsDone = false;
                int statisticsFailures = 0;
                while (statisticsDone == false && statisticsFailures < 5)
                {
                    try
                    {
                        Log("Updating statistics...");
                        var finish = Request<TaskStatus>("Update/UpdateFinished/");
                        if (finish != null)
                        {
                            Log(string.Format("{0}: {1}", finish.message, finish.status));
                            statisticsDone = finish.status;
                        }
                    }
                    catch (Exception e)
                    {
                        statisticsFailures++;
                        Log(e.Message);
                    }
                }


            }
            Log(string.Format("Run finished, {0} failures", failures));
            return failures;
        }

        public static int UpdatePlayers()
        {
            var failures = 0;

            Log(string.Format("Run started, connecting to {0}", FWAStatsURL));
            var index = Request<List<string>>("Update/PlayerBatch");

            if (index != null && index.Count > 0)
            {
                var queue = new BlockingCollection<string>();
                var failedQueue = new BlockingCollection<string>();

                foreach (var tag in index)
                {
                    queue.Add(tag);
                }
                queue.CompleteAdding();

                if (ServicePointManager.DefaultConnectionLimit < Environment.ProcessorCount)
                    ServicePointManager.DefaultConnectionLimit = Environment.ProcessorCount;

                Log(string.Format("Processing {0} players with {1} threads", index.Count, Environment.ProcessorCount));

                var tasks = new List<Task<int>>();
                for (var i = 0; i < Environment.ProcessorCount; i++)
                {
                    tasks.Add(Task.Run(() => PerformPlayerUpdate(queue)));
                    Thread.Sleep(2000);
                }

                foreach (var task in tasks)
                {
                    failures += task.Result;
                }
            }
            Log(string.Format("Player update finished, {0} failures", failures));
            return failures;
        }

        public static int PerformClanUpdate(BlockingCollection<UpdateTask> queue, BlockingCollection<UpdateTask> failQueue = null)
        {
            int failures = 0;
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out UpdateTask task))
                {
                    try
                    {
                        var status = Request<TaskStatus>(string.Format("Update/UpdateTask/{0}", task.id));
                        if (status != null)
                        {
                            Log(string.Format("{0}: {1}: {2}: {3}", Thread.CurrentThread.ManagedThreadId, queue.Count, status.message, status.status));
                            if (!status.status)
                                failures++;
                        }
                    }
                    catch (Exception e)
                    {
                        Log(string.Format("{0}: {1}: {2} ({3})", Thread.CurrentThread.ManagedThreadId, task.clanName, e.Message, task.id));
                        failures++;
                        if (failQueue != null)
                        {
                            if (failQueue.TryAdd(task))
                                failures--;
                        }
                    }
                }
            }
            return failures;
        }

        public static int PerformPlayerUpdate(BlockingCollection<string> queue)
        {
            int failures = 0;
            while (!queue.IsCompleted)
            {

                if (queue.TryTake(out string tag))
                {
                    try
                    {
                        var status = Request<TaskStatus>(string.Format("Update/UpdatePlayerTask/{0}", Uri.EscapeDataString(tag)));
                        if (status != null)
                        {
                            Log(string.Format("{0}: {1}: {2}: {3}", Thread.CurrentThread.ManagedThreadId, queue.Count, status.message, status.status));
                            if (!status.status)
                                failures++;
                        }
                    }
                    catch (Exception e)
                    {
                        Log(string.Format("{0}: {1}: {2}", Thread.CurrentThread.ManagedThreadId, tag, e.Message));
                        failures++;
                    }
                }
            }
            return failures;
        }

        public static void Log(string message)
        {
            Console.WriteLine(message);
        }

        static int Main(string[] args)
        {
            bool readUrl = false;
            foreach(var arg in args )
            {
                if (readUrl)
                {
                    FWAStatsURL = arg;
                    readUrl = false;
                }
                else if (arg == "-url")
                {
                    readUrl = true;
                }
                else
                {
                    Console.Error.WriteLine("Unknown parameter: {0}", arg);
                }
            }
            int clanErrors = UpdateClans();
            int playerErrors = UpdatePlayers();
            Log(string.Format("{0} clan update errors, {1} player update errors", clanErrors, playerErrors));
            return clanErrors + playerErrors;
        }
    }
}
