using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;

namespace ConsoleApplication
{
    public class Program
    {
        //TODO
        //Read in all important information like keys from config file (ini)
        //Get top 1000 channels by viewercount by name
        //Connect to all 1000 chat channels
        //Identify all emotes
        //Accrue emote counts per minute
        //Print table of emotes (basic)
        //Handle channel streaming ending
        //Handle top 1000 changing (maybe streams with > 1000 viewers at any point?)
        public static void Main(string[] args)
        {
            //Essentially the same thing on a console app
            //Task.WaitAll(GetAPI(), ConnectIRC());
            Task.WhenAll(GetAPI(), 
                         ConnectIRC())
                         .GetAwaiter().GetResult();

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

                string requestURL = "/kraken/streams";
                HttpResponseMessage httpResponseMessage = await httpClient.GetAsync(requestURL);
                string response = await httpResponseMessage.Content.ReadAsStringAsync();
                dynamic jsonResponse = JsonConvert.DeserializeObject(response);
                foreach (dynamic stream in jsonResponse["streams"])
                {
                    Console.WriteLine($"Game: {stream["game"],-40} Viewers: {stream["viewers"],-10} Channel: {stream["channel"]["display_name"]} ");
                }
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
                        await streamWriter.WriteLineAsync("JOIN #twitchpresents");
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
    }
}