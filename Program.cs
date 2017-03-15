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

namespace ConsoleApplication
{
    public class Program
    {
        //TODO
        //Get top 1000 channels by viewercount by name
        //Put 1000 names somewhere useful
        //Connect to all 1000 chat channels
        //Identify all emotes
        //Accrue emote counts per minute
        //Print table of emotes (basic)
        //Handle channel streaming ending
        //Handle top 1000 changing (maybe streams with > 1000 viewers at any point?)
        static ConcurrentDictionary<long, string> concurrentDictionary = new ConcurrentDictionary<long, string>();
        public static void Main(string[] args)
        {
            //Essentially the same thing on a console app
            //Task.WaitAll(GetAPI(), ConnectIRC());
            Task.WhenAll(GetAPI()).GetAwaiter().GetResult();
            Task.WhenAll(ConnectIRC()).GetAwaiter().GetResult();

            Console.WriteLine("Complete");
        }

        public static async Task GetAPI()
        {
            var configuration = new ConfigurationBuilder().AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini"))).Build();
            if (string.IsNullOrEmpty(configuration["twitchapi:uri"]) || string.IsNullOrEmpty(configuration["twitchapi:headersaccept"]) ||
                string.IsNullOrEmpty(configuration["twitchapi:headersclientid"]))
            {
                return;
            }

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(configuration["twitchapi:uri"]);

                httpClient.DefaultRequestHeaders.Add("Accept", configuration["twitchapi:headersaccept"]);
                httpClient.DefaultRequestHeaders.Add("Client-ID", configuration["twitchapi:headersclientid"]);
                short[] repeat = new short[1/*0*/] { 0/*, 99, 199, 299, 399, 499, 599, 699, 799, 899*/}; // Spare API for not until rate limiting in place
                foreach (short offset in repeat)
                {
                    string response = await RequestURL("/kraken/streams/", new Dictionary<string, string>() { { "limit", "10"/*100*/ }, { "offset", offset.ToString() } }, httpClient);
                    PrintResponse(response, offset);
                }
            }
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
                Console.WriteLine($"Rank: {++rank, -5} Game: {stream["game"],-50} Viewers: {stream["viewers"],-7} Channel: {stream["channel"]["display_name"]} ");
                concurrentDictionary[(long)stream["channel"]["_id"]] = (string)stream["channel"]["name"];
            }
        }

        public static async Task ConnectIRC()
        {
            var configuration = new ConfigurationBuilder().AddIniFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\config.ini"))).Build();
            if (string.IsNullOrEmpty(configuration["twitchirc:servername"]) || string.IsNullOrEmpty(configuration["twitchirc:portnumber"]) ||
                string.IsNullOrEmpty(configuration["twitchirc:oauth"]) || string.IsNullOrEmpty(configuration["twitchirc:nick"]))
            {
                return;
            }

            using (TcpClient tcpClient = new TcpClient())
            {
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

                        while (true)
                        {
                            string readLine = await streamReader.ReadLineAsync();
                            if (readLine == "PING :tmi.twitch.tv")
                            {
                                await streamWriter.WriteLineAsync("PONG :time.twitch.tv");
                            }
                            Console.WriteLine(readLine);
                        }
                    }
                }
            }
        }

        public static string ToQueryString(Dictionary<string, string> source)
        {
            return string.Format("?{0}", String.Join("&", source.Select(kvp => String.Format("{0}={1}", kvp.Key, kvp.Value))));
        }
    }
}