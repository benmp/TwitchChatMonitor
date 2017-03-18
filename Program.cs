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
        //Identify all emotes through api /chat
        //parse received channel messages looking for all emote in PRIVMSG only
        //print to console screen on grade % scale with ....| graphics or something cool
            //make it use console.width to determine
            //see if I can set console.height to fit top 50 emotes

        //Handle channel streaming ending
        //Handle top 1000 changing (maybe streams with > 1000 viewers at any point?)

        //refactor classes

        // Console.WriteLine("Request URL before await : " + Thread.CurrentThread.ManagedThreadId);
        static ConcurrentDictionary<long, string> concurrentDictionary = new ConcurrentDictionary<long, string>();
        static ConcurrentQueue<DateTime> concurrentQueue = new ConcurrentQueue<DateTime>();

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

        public static async Task MainAsync()
        {
            #if debug
            Console.WriteLine("Main thread before GetAPI await : " + Thread.CurrentThread.ManagedThreadId);
            #endif
            await GetAPI();
            #if debug
            Console.WriteLine("Main thread after GetAPI await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            StatisticsService ss = new StatisticsService();
            ss.StartService();

            #if debug
            Console.WriteLine("Main thread before ReportStatistics : " + Thread.CurrentThread.ManagedThreadId);
            #endif
            //intentionally not awaited so we can fire and forget our while loop, yields to this calling code after first await
            var ignore = ss.ReportStatistics();
            #if debug
            Console.WriteLine("Main thread after ReportStatistics: " + Thread.CurrentThread.ManagedThreadId);
            #endif

            await ConnectIRC(ss);//Await this loop its our main logic loop
        }

        public static async Task GetAPI()
        {
                               short[] repeat = new short[1/*0*/] { 0/*, 99, 199, 299, 399, 499, 599, 699, 799, 899*/}; // Spare API for not until rate limiting in place
                foreach (short offset in repeat)
                {
                    string response = await RequestURL("/kraken/streams/", new Dictionary<string, string>() { { "limit", "10"/*100*/ }, { "offset", offset.ToString() } }, httpClient);
                    PrintResponse(response, offset);
                }
                #if debug
                Console.WriteLine("GetAPI after await : " + Thread.CurrentThread.ManagedThreadId);
                #endif
        }

        public static async Task<string> RequestURL(string url, Dictionary<string, string> queryString, HttpClient httpClient)
        {
            string requestURL = string.Format("{0}{1}", url, ToQueryString(queryString));
            HttpResponseMessage httpResponseMessage = await httpClient.GetAsync(requestURL);
            return await httpResponseMessage.Content.ReadAsStringAsync();
        }

        public static void PrintResponse(string response, short rank)
        {
            dynamic jsonResponse = JsonConvert.DeserializeObject(response);
            foreach (dynamic stream in jsonResponse["streams"])
            {
                Console.WriteLine($"Rank: {++rank,-5} Game: {stream["game"],-50} Viewers: {stream["viewers"],-7} Channel: {stream["channel"]["display_name"]} ");
                concurrentDictionary[(long)stream["channel"]["_id"]] = (string)stream["channel"]["name"];
            }
        }

        public static async Task ConnectIRC(StatisticsService ss)
        {
            var configuration = new ConfigurationBuilder().AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini"))).Build();
            if (string.IsNullOrEmpty(configuration["twitchirc:servername"]) || string.IsNullOrEmpty(configuration["twitchirc:portnumber"]) ||
                string.IsNullOrEmpty(configuration["twitchirc:oauth"]) || string.IsNullOrEmpty(configuration["twitchirc:nick"]))
            {
                return;
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
            IConfiguration configuration = new ConfigurationBuilder()
                .AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini")))
                .Build();

            return (!string.IsNullOrEmpty(configuration["twitchapi:uri"]) || 
                     string.IsNullOrEmpty(configuration["twitchapi:headersaccept"]) ||
                     string.IsNullOrEmpty(configuration["twitchapi:headersclientid"]));
        }

        public bool BuildChannelDictionary(short numberOfChannels)
        {
            #if debug
            Console.WriteLine("BuildChannelDictionary before await : " + Thread.CurrentThread.ManagedThreadId);
            #endif

            httpClient.BaseAddress = new Uri(configuration["twitchapi:uri"]);

            httpClient.DefaultRequestHeaders.Add("Accept", configuration["twitchapi:headersaccept"]);
            httpClient.DefaultRequestHeaders.Add("Client-ID", configuration["twitchapi:headersclientid"]);

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
        public void StartService()
        {
            List<string> emotes = new List<string>() { "PogChamp", "4Head", "BibleThump", "FrankerZ", "BabyRage", "BrokeBack", "Jebaited", "Kappa", "Kreygasm" };
            foreach (string emote in emotes)
            {
                iStatCrunchers.Add(new EmoteStatCruncher(emote));
            }
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
                foreach (IStatCruncher iStatCruncher in iStatCrunchers)
                {
                    iStatCruncher.ReportStatistics();
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
        void ReportStatistics();
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
        public void ReportStatistics()
        {
            RefreshStatistics();
            Console.WriteLine($"{StringOfInterest,-10}: {Occurrences.Count}");
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
}