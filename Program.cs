#define debug

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;

namespace ConsoleApplication
{
    public class Program
    {
        //TODO
        //parse received channel messages looking for all emote in PRIVMSG only
        //print to console screen on grade % scale with ....| graphics or something cool
            //make it use console.width to determine
            //see if I can set console.height to fit top 50 emotes

        //Handle channel streaming ending
        //Handle top 1000 changing (maybe streams with > 1000 viewers at any point?)

        //refactor classes

        // Console.WriteLine("Request URL before await : " + Thread.CurrentThread.ManagedThreadId);
        public static ConcurrentDictionary<long, string> concurrentDictionary = new ConcurrentDictionary<long, string>();
        public static ConcurrentQueue<DateTime> concurrentQueue = new ConcurrentQueue<DateTime>();

        public static void Main(string[] args)
        {
            AsyncPump.Run(async delegate
            {
                #if debug
                Console.WriteLine("Main thread before await : " + Thread.CurrentThread.ManagedThreadId);
                #endif
                await MainAsync();
            });
            Console.WriteLine("Complete");
        }

        public static async Task<string> MainAsync()
        {
            #if debug
            Console.WriteLine("Main thread before GetAPI await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            TwitchAPI twitchAPI = new TwitchAPI();
            twitchAPI.BuildConfiguration();
            twitchAPI.SetupHttpClient();

            bool success = await twitchAPI.BuildChannelDictionary();
            if (!success)
            {
                return "Error connecting to API";
            }

            #if debug
            Console.WriteLine("Main thread after GetAPI await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            StatisticsService ss = new StatisticsService();
            success = await ss.StartService(twitchAPI);
            if (!success)
            {
                return "Error getting emoticons";
            }

            #if debug
            Console.WriteLine("Main thread before ReportStatistics : " + Thread.CurrentThread.ManagedThreadId);
            #endif
            //intentionally not awaited so we can fire and forget our while loop, yields to this calling code after first await
            var ignore = ss.ReportStatistics();
            #if debug
            Console.WriteLine("Main thread after ReportStatistics: " + Thread.CurrentThread.ManagedThreadId);
            #endif

            success = await ConnectIRC(ss);//Await this loop its our main logic loop
            if (!success)
            {
                return "Problem connected to IRC";
            }

            return "complete";
        }

        public static async Task<bool> ConnectIRC(StatisticsService ss)
        {
            var configuration = new ConfigurationBuilder().AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini"))).Build();
            if (string.IsNullOrEmpty(configuration["twitchirc:servername"]) || string.IsNullOrEmpty(configuration["twitchirc:portnumber"]) ||
                string.IsNullOrEmpty(configuration["twitchirc:oauth"]) || string.IsNullOrEmpty(configuration["twitchirc:nick"]))
            {
                return false;
            }

            using (TcpClient tcpClient = new TcpClient())
            {
                #if debug
                Console.WriteLine("ConnectIRC before await : " + Thread.CurrentThread.ManagedThreadId);
                #endif
                await tcpClient.ConnectAsync(configuration["twitchirc:servername"], Convert.ToInt32(configuration["twitchirc:portnumber"]));
                using (StreamReader streamReader = new StreamReader(tcpClient.GetStream()))
                {
                    using (StreamWriter streamWriter = new StreamWriter(tcpClient.GetStream()))
                    {
                        streamWriter.AutoFlush = true;
                        await streamWriter.WriteLineAsync($"PASS oauth:{configuration["twitchirc:oauth"]}");
                        await streamWriter.WriteLineAsync($"NICK {configuration["twitchirc:nick"]}");

                        foreach (string channelName in concurrentDictionary.Values)
                        {
                            await streamWriter.WriteLineAsync($"JOIN #{channelName}");
                        }

                        #if debug
                        Console.WriteLine("ConnectIRC after await : " + Thread.CurrentThread.ManagedThreadId);
                        #endif
                        while (true)
                        {
                            string readLine = await streamReader.ReadLineAsync();
                            if (readLine == "PING :tmi.twitch.tv")
                            {
                                await streamWriter.WriteLineAsync("PONG :time.twitch.tv");
                            }

                            if (string.IsNullOrEmpty(readLine))
                            {
                                return false;
                            }
                            ss.DistributeInformation(readLine);
                        }
                    }
                }
            }
        }
    }

    public class TwitchAPI
    {
        public static HttpClient httpClient { get; } = new HttpClient();
        public IConfiguration configuration { get; set; } = null;
        public static string ToQueryString(Dictionary<string, string> source)
        {
            return string.Format("?{0}", String.Join("&", source.Select(kvp => String.Format("{0}={1}", kvp.Key, kvp.Value))));
        }

        public bool BuildConfiguration()
        {
            configuration = new ConfigurationBuilder()
                .AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini")))
                .Build();

            return (!string.IsNullOrEmpty(configuration["twitchapi:uri"]) || 
                     string.IsNullOrEmpty(configuration["twitchapi:headersaccept"]) ||
                     string.IsNullOrEmpty(configuration["twitchapi:headersclientid"]));
        }

        public void SetupHttpClient()
        {
            httpClient.BaseAddress = new Uri(configuration["twitchapi:uri"]);

            httpClient.DefaultRequestHeaders.Add("Accept", configuration["twitchapi:headersaccept"]);
            httpClient.DefaultRequestHeaders.Add("Client-ID", configuration["twitchapi:headersclientid"]);
        }

        public async Task<bool> BuildChannelDictionary()
        {
            #if debug
            Console.WriteLine("BuildChannelDictionary before await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            short[] repeat = new short[1/*0*/] { 0/*, 99, 199, 299, 399, 499, 599, 699, 799, 899*/}; // Spare API for not until rate limiting in place
            foreach (short offset in repeat)
            {
                string requestURL = string.Format("{0}{1}", "/kraken/streams/", ToQueryString(new Dictionary<string, string>() { { "limit", "100" }, { "offset", offset.ToString() } }));
                HttpResponseMessage httpResponseMessage = await httpClient.GetAsync(requestURL);

                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    return false;
                }

                string response = await httpResponseMessage.Content.ReadAsStringAsync();
                dynamic jsonResponse = JsonConvert.DeserializeObject(response);
                foreach (dynamic stream in jsonResponse["streams"])
                {
                    Program.concurrentDictionary[(long)stream["channel"]["_id"]] = (string)stream["channel"]["name"];
                }
            }

            #if debug
            Console.WriteLine("BuildChannelDictionary after await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            return true;
        }

        public async Task<List<string>> GetChatEmotes()
        {
            List<string> emotes = new List<string>();

            #if debug
            Console.WriteLine("GetChatEmotes before await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            HttpResponseMessage httpResponseMessage = await httpClient.GetAsync("/kraken/chat/emoticons/");

            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                return emotes;
            }

            string response = await httpResponseMessage.Content.ReadAsStringAsync();
            dynamic jsonResponse = JsonConvert.DeserializeObject(response);
            foreach (dynamic emoticons in jsonResponse["emoticons"])
            {
                foreach (dynamic emoticon in emoticons)
                {
                    foreach (dynamic regex in emoticon)
                    {
                        emotes.Add(regex.ToString());
                        break;
                    }
                    break;
                }
            }

            #if debug
            Console.WriteLine("GetChatEmotes after await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            return emotes;
        }
    }
    public interface IMessageParser
    {
        int NumberOfOccurences(string message, string stringOfInterest);
    }

    public class WholeLineMessageParser : IMessageParser
    {
        public int NumberOfOccurences(string message, string stringOfInterest)
        {
            return message.Contains(stringOfInterest) ? 1 : 0;
        }
    }

    public class StatisticsService
    {
        HashSet<IStatCruncher> iStatCrunchers = new HashSet<IStatCruncher>();
        public async Task<bool> StartService(TwitchAPI twitchAPI)
        {
            List<string> emotes = await twitchAPI.GetChatEmotes();
            if (emotes.Count == 0)
            {
                return false;
            }

            foreach (string emote in emotes.Where(e => e.Length > 1)) //TODO why to S R and Q showup?
            {
                iStatCrunchers.Add(new EmoteStatCruncher(emote));
            }

            return true;
        }

        public void DistributeInformation(string information)
        {
            foreach (IStatCruncher iStatCruncher in iStatCrunchers)
            {
                iStatCruncher.TryEnqueueMessage(information);
            }
        }

        public async Task ReportStatistics()
        {
            while (true)
            {
                Console.WriteLine();
                List<StatisticsResult> statResults = new List<StatisticsResult>();
                foreach (IStatCruncher iStatCruncher in iStatCrunchers)
                {
                    statResults.Add(iStatCruncher.ReportStatistics());
                }

                foreach (StatisticsResult statResult in statResults.OrderByDescending(sr => sr.NumberOfOccurrences).Take(20))
                {
                    Console.WriteLine($"{statResult.StringOfInterest,-10}: {statResult.NumberOfOccurrences}");
                }

                #if debug
                Console.WriteLine("ReportStatistics before await : " + Thread.CurrentThread.ManagedThreadId);
                #endif
                await Task.Delay(5000);
                #if debug
                Console.WriteLine("ReportStatistics after await : " + Thread.CurrentThread.ManagedThreadId);
                #endif
            }
        }
    }

    public interface IStatCruncher
    {
        void TryEnqueueMessage(string message);
        StatisticsResult ReportStatistics();
    }

    public class EmoteStatCruncher : IStatCruncher
    {
        public EmoteStatCruncher(string stringOfInterest)
        {
            StringOfInterest = stringOfInterest;
            Occurrences = new ConcurrentQueue<DateTime>();
            MessageParser = new WholeLineMessageParser();
        }
        public string StringOfInterest { get; set; }
        ConcurrentQueue<DateTime> Occurrences { get; set; }
        IMessageParser MessageParser { get; set; }
        void IStatCruncher.TryEnqueueMessage(string message)
        {
            int numberOfOccurrences = MessageParser.NumberOfOccurences(message, StringOfInterest);
            for (int i = 0; i < numberOfOccurrences; i++)
            {
                Occurrences.Enqueue(DateTime.Now);
            }
        }
        public StatisticsResult ReportStatistics()
        {
            RefreshStatistics();
            return new StatisticsResult() { NumberOfOccurrences = Occurrences.Count, StringOfInterest = StringOfInterest };
        }
        void RefreshStatistics()
        {
            DateTime frontOfQueue = DateTime.MinValue;
            if (Occurrences.TryPeek(out frontOfQueue))
            {
                if (DateTime.Now.Subtract(frontOfQueue).TotalSeconds > 60)
                {
                    Occurrences.TryDequeue(out frontOfQueue);
                    RefreshStatistics();
                }
            }
        }
    }

    public struct StatisticsResult
    {
        public string StringOfInterest;
        public int NumberOfOccurrences;
    }
}