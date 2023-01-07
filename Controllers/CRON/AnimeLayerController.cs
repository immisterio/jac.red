﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using JacRed.Engine;
using JacRed.Engine.CORE;
using JacRed.Engine.Parse;
using JacRed.Models.tParse;
using Microsoft.AspNetCore.Mvc;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/animelayer/[action]")]
    public class AnimeLayerController : BaseController
    {
        #region TakeLogin
        static string Cookie { get; set; }

        async public static Task<bool> TakeLogin()
        {
            try
            {
                var clientHandler = new System.Net.Http.HttpClientHandler()
                {
                    AllowAutoRedirect = false
                };

                clientHandler.ServerCertificateCustomValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (var client = new System.Net.Http.HttpClient(clientHandler))
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.MaxResponseContentBufferSize = 2000000; // 2MB
                    client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/75.0.3770.100 Safari/537.36");

                    var postParams = new Dictionary<string, string>
                    {
                        { "login", AppInit.conf.Animelayer.login.u },
                        { "password", AppInit.conf.Animelayer.login.p }
                    };

                    using (var postContent = new System.Net.Http.FormUrlEncodedContent(postParams))
                    {
                        using (var response = await client.PostAsync($"{AppInit.conf.Animelayer.host}/auth/login/", postContent))
                        {
                            if (response.Headers.TryGetValues("Set-Cookie", out var cook))
                            {
                                string layer_id = null, layer_hash = null, PHPSESSID = null;
                                foreach (string line in cook)
                                {
                                    if (string.IsNullOrWhiteSpace(line))
                                        continue;

                                    if (line.Contains("layer_id="))
                                        layer_id = new Regex("layer_id=([^;]+)(;|$)").Match(line).Groups[1].Value;

                                    if (line.Contains("layer_hash="))
                                        layer_hash = new Regex("layer_hash=([^;]+)(;|$)").Match(line).Groups[1].Value;

                                    if (line.Contains("PHPSESSID="))
                                        PHPSESSID = new Regex("PHPSESSID=([^;]+)(;|$)").Match(line).Groups[1].Value;
                                }

                                if (!string.IsNullOrWhiteSpace(layer_id) && !string.IsNullOrWhiteSpace(layer_hash) && !string.IsNullOrWhiteSpace(PHPSESSID))
                                {
                                    Cookie = $"layer_id={layer_id}; layer_hash={layer_hash}; PHPSESSID={PHPSESSID};";
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }
        #endregion


        #region Parse
        static bool workParse = false;

        async public Task<string> Parse(int maxpage = 1)
        {
            #region Авторизация
            if (Cookie == null)
            {
                if (await TakeLogin() == false)
                    return "Не удалось авторизоваться";
            }
            #endregion

            if (workParse)
                return "work";

            workParse = true;

            try
            {
                for (int page = 1; page <= maxpage; page++)
                    await parsePage(page);
            }
            catch { }

            workParse = false;
            return "ok";
        }
        #endregion


        #region parsePage
        async Task<bool> parsePage(int page)
        {
            string html = await HttpClient.Get($"{AppInit.conf.Animelayer.host}/torrents/anime/?page={page}", useproxy: AppInit.conf.Animelayer.useproxy);
            if (html == null || !html.Contains("id=\"wrapper\""))
                return false;

            foreach (string row in tParse.ReplaceBadNames(HttpUtility.HtmlDecode(html.Replace("&nbsp;", ""))).Split("class=\"torrent-item torrent-item-medium panel\"").Skip(1))
            {
                #region Локальный метод - Match
                string Match(string pattern, int index = 1)
                {
                    string res = new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[index].Value.Trim();
                    res = Regex.Replace(res, "[\n\r\t ]+", " ");
                    return res.Trim();
                }
                #endregion

                if (string.IsNullOrWhiteSpace(row))
                    continue;

                #region Дата создания
                DateTime createTime = default;

                if (Regex.IsMatch(row, "(Добавл|Обновл)[^<]+</span>[0-9]+ [^ ]+ [0-9]{4}"))
                {
                    createTime = tParse.ParseCreateTime(Match(">(Добавл|Обновл)[^<]+</span>([0-9]+ [^ ]+ [0-9]{4})", 2), "dd.MM.yyyy");
                }
                else
                {
                    string date = Match("(Добавл|Обновл)[^<]+</span>([^\n]+) в", 2);
                    if (string.IsNullOrWhiteSpace(date))
                        continue;

                    createTime = tParse.ParseCreateTime($"{date} {DateTime.Today.Year}", "dd.MM.yyyy");
                }

                if (createTime == default)
                    continue;
                #endregion

                #region Данные раздачи
                var gurl = Regex.Match(row, "<a href=\"/(torrent/[a-z0-9]+)/?\">([^<]+)</a>").Groups;

                string url = gurl[1].Value;
                string title = gurl[2].Value;

                string _sid = Match("class=\"icon s-icons-upload\"></i>([0-9]+)");
                string _pir = Match("class=\"icon s-icons-download\"></i>([0-9]+)");

                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                    continue;

                if (Regex.IsMatch(row, "Разрешение: ?</strong>1920x1080"))
                    title += " [1080p]";
                else if (Regex.IsMatch(row, "Разрешение: ?</strong>1280x720"))
                    title += " [720p]";

                url = $"{AppInit.conf.Animelayer.host}/{url}/";
                #endregion

                #region name / originalname
                string name = null, originalname = null;

                // Shaman king (2021) / Король-шаман [ТВ] (1-7)
                var g = Regex.Match(title, "([^/\\[\\(]+)\\([0-9]{4}\\)[^/]+/([^/\\[\\(]+)").Groups;
                if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                {
                    name = g[2].Value.Trim();
                    originalname = g[1].Value.Trim();
                }
                else
                {
                    // Shadows House / Дом теней (1—6)
                    g = Regex.Match(title, "^([^/\\[\\(]+)/([^/\\[\\(]+)").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                    {
                        name = g[2].Value.Trim();
                        originalname = g[1].Value.Trim();
                    }
                }
                #endregion

                // Год выхода
                if (!int.TryParse(Match("Год выхода: ?</strong>([0-9]{4})"), out int relased) || relased == 0)
                    continue;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    #region Обновляем/Получаем Magnet
                    string magnet = null;
                    string sizeName = null;

                    if (!tParse.TryGetValue(url, out TorrentDetails _tcache) || _tcache.title != title)
                    {
                        byte[] torrent = await HttpClient.Download($"{url}download/", cookie: Cookie);
                        magnet = BencodeTo.Magnet(torrent);
                        sizeName = BencodeTo.SizeName(torrent);
                    }
                    else
                    {
                        magnet = _tcache.magnet;
                    }

                    if (string.IsNullOrWhiteSpace(magnet))
                        continue;
                    #endregion

                    int.TryParse(_sid, out int sid);
                    int.TryParse(_pir, out int pir);

                    tParse.AddOrUpdate(new TorrentDetails()
                    {
                        trackerName = "animelayer",
                        types = new string[] { "anime" },
                        url = url,
                        title = title,
                        sid = sid,
                        pir = pir,
                        sizeName = sizeName,
                        createTime = createTime,
                        magnet = magnet,
                        name = name,
                        originalname = originalname,
                        relased = relased
                    });

                    await Task.Delay(AppInit.conf.Animelayer.parseDelay);
                }
            }

            return true;
        }
        #endregion
    }
}