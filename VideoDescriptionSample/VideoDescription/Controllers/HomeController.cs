﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using ImageResizer;
using VideoDescription.Models;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Configuration;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Newtonsoft.Json;
using System.Text;
using VideoDescription.Util;
using Newtonsoft.Json.Linq;
using VideoIndexerLibrary;
using TranslatorLibrary;
using System.Diagnostics;
using System.Globalization;

namespace VideoDescription.Controllers
{
    public class HomeController : Controller
    {
        private const string dirHighRes = "highres";
        private const string dirLowRes = "lowres";
        private const string dirInsights = "insights";
        private const string fileInsights = "insights.json";


        public JsonResult LongRunningProcess(string mode, string videoId, string viAcctID, string viSubKey, string viLocation, string translationLang)
        {
            ViewBag.ErrorMsg = String.Empty;//Reset error message
            // let's save the VI credentials intot the user sessions variable to display them when the page refresh
            HttpContextProvider.Current.Session["videoId"] = videoId;
            HttpContextProvider.Current.Session["VideoIndexerAccountId"] = viAcctID;
            HttpContextProvider.Current.Session["VideoIndexerSubscriptionKey"] = viSubKey;
            HttpContextProvider.Current.Session["VideoIndexerLocation"] = viLocation;
            HttpContextProvider.Current.Session["TranslationLang"] = translationLang;

            Task.Run(async () => await Importviscenes(mode, videoId, viAcctID, viSubKey, viLocation, translationLang).ConfigureAwait(false)).GetAwaiter().GetResult();

            return Json("", JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> Index()
        {
            // when the page loads for the first time, we load the default settings from the config file
            if (HttpContextProvider.Current.Session["VideoIndexerAccountId"] == null)
            {
                HttpContextProvider.Current.Session["VideoIndexerAccountId"] = ConfigurationManager.AppSettings["VideoIndexerAccountId"];
            }

            if (HttpContextProvider.Current.Session["VideoIndexerSubscriptionKey"] == null)
            {
                HttpContextProvider.Current.Session["VideoIndexerSubscriptionKey"] = ConfigurationManager.AppSettings["VideoIndexerSubscriptionKey"];
            }

            if (HttpContextProvider.Current.Session["VideoIndexerLocation"] == null)
            {
                HttpContextProvider.Current.Session["VideoIndexerLocation"] = ConfigurationManager.AppSettings["VideoIndexerLocation"];
            }

            if (HttpContextProvider.Current.Session["TranslationLang"] == null)
            {
                HttpContextProvider.Current.Session["TranslationLang"] = ConfigurationManager.AppSettings["TranslationLang"];
            }

            CloudStorageAccount account = null;
            try
            {
                account = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]);
            }
            catch (Exception exc)
            {
                ViewBag.ErrorMsg = exc.ToString();
                Functions.SendProgress("Error:" + exc.Message, 0, 1);
                return View();
            }

            CloudBlobClient client = account.CreateCloudBlobClient();
            string videoId = (string)System.Web.HttpContext.Current.Session["videoId"];

            // Pass a list of blob URIs in ViewBag
            List<BlobInfo> blobs = new List<BlobInfo>();

            if (videoId != null)
            {
                CloudBlobContainer container = client.GetContainerReference(videoId);

                List<IListBlobItem> blobsList = new List<IListBlobItem>();
                try
                {
                    blobsList = container.ListBlobs(prefix: dirHighRes + "/", useFlatBlobListing: true).ToList();
                }
                catch (Exception exc)
                {
                    ViewBag.ErrorMsg = exc.ToString();
                    videoId = null;
                    Functions.SendProgress("Error:" + exc.Message, 0, 1);
                }

                foreach (IListBlobItem item in blobsList)
                {
                    var blob = item as CloudBlockBlob;

                    if (blob != null)
                    {
                        blob.FetchAttributes(); // Get blob metadata
                        var description = blob.Metadata.ContainsKey("Description") ? blob.Metadata["Description"] : "(no description)";
                        var descriptionTranslated = blob.Metadata.ContainsKey("DescriptionTranslated") ? HttpUtility.HtmlDecode(blob.Metadata["DescriptionTranslated"]) : null;
                        string confidence = blob.Metadata.ContainsKey("Confidence") ? Double.Parse(blob.Metadata["Confidence"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "%" : null;
                        TimeSpan? adjustedStart = blob.Metadata.ContainsKey("AdjustedStart") ? (TimeSpan?)TimeSpan.Parse(blob.Metadata["AdjustedStart"], CultureInfo.InvariantCulture) : (TimeSpan?)null;
                        int? index = blob.Metadata.ContainsKey("Index") ? (int?)(int.Parse(blob.Metadata["Index"], CultureInfo.InvariantCulture)) : null;

                        blobs.Add(new BlobInfo()
                        {
                            ImageUri = blob.Uri.ToString(),
                            ThumbnailUri = blob.Uri.ToString().Replace("/" + dirHighRes + "/", "/" + dirLowRes + "/"),
                            Description = description,
                            DescriptionTranslated = descriptionTranslated != null ? Encoding.UTF8.GetString(Convert.FromBase64String(descriptionTranslated)) : null,
                            Confidence = confidence,
                            AdjustedStart = adjustedStart,
                            Index = index
                        });
                    }
                }

                var speakerS = ConfigurationManager.AppSettings["SpeakerStatsFeature"];
                if (speakerS != null && bool.Parse(speakerS))
                {
                    List<SpeakerStats> SpeakersStatsBuilder = new List<SpeakerStats>();

                    // Reading insights about speakers
                    CloudBlockBlob insightsBlob = container.GetBlockBlobReference(dirInsights + "/" + fileInsights);
                    try
                    {
                        string jsonData = await insightsBlob.DownloadTextAsync().ConfigureAwait(false);
                        dynamic jObj = JObject.Parse(jsonData);
                        dynamic speakers = jObj.videos[0].insights.speakers;
                        foreach (dynamic speaker in speakers)
                        {
                            TimeSpan duration = new TimeSpan();
                            foreach (dynamic inst in speaker.instances)
                            {
                                duration += DateTime.Parse((string)inst.end, CultureInfo.InvariantCulture) - DateTime.Parse((string)inst.start, CultureInfo.InvariantCulture);
                            }
                            SpeakersStatsBuilder.Add(new SpeakerStats() { Name = (string)speaker.name, Duration = duration });
                        }
                        ViewBag.SpeakerStats = SpeakersStatsBuilder.Count > 0 ? SpeakersStatsBuilder.OrderByDescending(s => s.Duration) : null;
                    }
                    catch
                    {

                    }
                }
            }

            ViewBag.Blobs = blobs.OrderBy(bl => bl.Index).ToArray();
            ViewBag.VideoId = videoId;

            ViewBag.VideoIndexerAccountId = HttpContextProvider.Current.Session["VideoIndexerAccountId"];
            ViewBag.VideoIndexerSubscriptionKey = HttpContextProvider.Current.Session["VideoIndexerSubscriptionKey"];
            ViewBag.VideoIndexerLocation = HttpContextProvider.Current.Session["VideoIndexerLocation"];
            ViewBag.TranslationLang = HttpContextProvider.Current.Session["TranslationLang"];

            if (videoId != null)
            {
                try
                {
                    VideoIndexer myVI = new VideoIndexer(
                         (string)HttpContextProvider.Current.Session["VideoIndexerAccountId"],
                         (string)HttpContextProvider.Current.Session["VideoIndexerLocation"],
                         (string)HttpContextProvider.Current.Session["VideoIndexerSubscriptionKey"]);

                    ViewBag.VideoAccessToken = Task.Run(async () => await myVI.GetVideoAccessTokenAsync(videoId).ConfigureAwait(false)).GetAwaiter().GetResult();
                    ViewBag.PlayerWidgetUrl = Task.Run(async () => await myVI.GetPlayerWidgetAsync(videoId).ConfigureAwait(false)).GetAwaiter().GetResult();
                    ViewBag.VideoInsightsWidgetUrl = Task.Run(async () => await myVI.GetVideoInsightsWidgetAsync(videoId, false).ConfigureAwait(false)).GetAwaiter().GetResult();

                }
                catch (Exception exc)
                {
                    ViewBag.ErrorMsg = exc.ToString();
                    Functions.SendProgress("Error in VideoIndexer:" + exc.Message, 0, 1);
                }
            }

            return View();
        }



        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";

            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";

            return View();
        }


        // mode : "shots", "scenes", "purge", "load"
        //  [HttpPost]
        public async Task Importviscenes(string mode, string videoId, string viAcctID, string viSubKey, string viLocation, string translationLang)
        {
            if (mode == "load") return;

            Functions.SendProgress("Cleaning...", 0, 1);

            // Pass a list of blob URIs in ViewBag
            CloudStorageAccount account = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]);
            CloudBlobClient client = account.CreateCloudBlobClient();

            CloudBlobContainer containerVideoId = client.GetContainerReference(videoId);
            // Let's purge the data

            // let's purge all existing blobs
            List<IListBlobItem> blobsList = new List<IListBlobItem>();
            try
            {
                blobsList = containerVideoId.ListBlobs().ToList();
                List<Task> myTasks = new List<Task>();
                foreach (IListBlobItem item in containerVideoId.ListBlobs(useFlatBlobListing: true))
                {
                    var blob = item as CloudBlockBlob;

                    if (blob != null)
                    {
                        myTasks.Add(blob.DeleteAsync());
                    }
                }
                await Task.WhenAll(myTasks.ToArray()).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                ViewBag.ErrorMsg = exc.ToString();
                Functions.SendProgress("Error in ListBlobs:" + exc.Message, 0, 1);
            }

            // user wants only purge
            if (mode == "purge") return;

            Functions.SendProgress("Initialization...", 0, 1);

            await containerVideoId.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, null, null).ConfigureAwait(false);

            // Translator credentials
            string translatorSubscriptionKey = ConfigurationManager.AppSettings["TranslatorSubscriptionKey"];
            string translatorEndpoint = ConfigurationManager.AppSettings["TranslatorEndpoint"];

            // Computer vision init
            ComputerVisionClient vision = new ComputerVisionClient(
                                                                    new ApiKeyServiceClientCredentials(ConfigurationManager.AppSettings["VisionSubscriptionKey"]),
                                                                    new System.Net.Http.DelegatingHandler[] { }
                                                                    );
            vision.Endpoint = ConfigurationManager.AppSettings["VisionEndpoint"];

            VisualFeatureTypes[] features = new VisualFeatureTypes[] { VisualFeatureTypes.Description };

            // test code get all thumbnails
            string jsonData = "";
            VideoIndexer myVI = new VideoIndexer(viAcctID, viLocation, viSubKey);

            try
            {
                //videoToken = await myVI.GetVideoAccessTokenAsync(videoId).ConfigureAwait(false);
                jsonData = await myVI.GetInsightsAsync(videoId).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                ViewBag.ErrorMsg = exc.ToString();
                Functions.SendProgress("Error in GetInsightsAsync:" + exc.Message, 0, 1);

                return;
            }

            // SAVING INSIGHTS AS A BLOB
            CloudBlockBlob insightsBlob = containerVideoId.GetBlockBlobReference(dirInsights + "/" + fileInsights);
            JObject jObj = JObject.Parse(jsonData);
            await insightsBlob.UploadTextAsync(jObj.ToString(Formatting.Indented)).ConfigureAwait(false);

            List<BlobInfo> blobs = new List<BlobInfo>();

            Dictionary<TimeSpan, string> shotsTimingAndThumbnailsId = new Dictionary<TimeSpan, string>();
            Dictionary<TimeSpan, string> scenesTimingAndThumbnailId = new Dictionary<TimeSpan, string>();
            dynamic viInsights = JsonConvert.DeserializeObject<dynamic>(jsonData);
            var video = viInsights.videos[0];
            var shots = video.insights.shots;
            var scenes = video.insights.scenes;

            // list of shots
            foreach (var shot in shots)
            {
                foreach (var keyFrame in shot.keyFrames)
                {
                    foreach (var instance in keyFrame.instances)
                    {
                        string thumbnailId = (string)instance.thumbnailId;
                        string thumbnailStartTime = (string)instance.adjustedStart;
                        shotsTimingAndThumbnailsId.Add(TimeSpan.Parse(thumbnailStartTime, CultureInfo.InvariantCulture), thumbnailId);
                    }
                }
            }
            var listTimings = shotsTimingAndThumbnailsId.Select(d => d.Key).ToList().OrderBy(d => d);

            //list of scenes (a scene contains several shots, but in the JSON, thumbnails are not defined in scenes)
            if (scenes != null) // sometimes, there is no scene !
            {
                foreach (var scene in scenes)
                {
                    TimeSpan start = TimeSpan.Parse((string)scene.instances[0].adjustedStart, CultureInfo.InvariantCulture);
                    var closestTime = listTimings.OrderBy(t => Math.Abs((t - start).Ticks))
                                       .First();
                    scenesTimingAndThumbnailId.Add(closestTime, shotsTimingAndThumbnailsId[closestTime]);
                }
            }

            // it's the list of thumbnails we want to process (scenes or all shots)
            Dictionary<TimeSpan, string> thumbnailsToProcessTimeAndId = new Dictionary<TimeSpan, string>();

            if (mode == "scenes") // scenes only
            {
                if (scenes == null) // no scenes, let's quit
                {
                    Functions.SendProgress($"No scenes !", 10, 10);
                    return;
                }
                thumbnailsToProcessTimeAndId = scenesTimingAndThumbnailId;
            }
            else // all shots
            {
                thumbnailsToProcessTimeAndId = shotsTimingAndThumbnailsId; ;
            }

            int index = 0;
            foreach (var thumbnailEntry in thumbnailsToProcessTimeAndId)
            {
                Functions.SendProgress($"Processing {thumbnailsToProcessTimeAndId.Count} thumbnails...", index, thumbnailsToProcessTimeAndId.Count);
                index++;
                //if (index == 100) break;

                string thumbnailId = thumbnailEntry.Value;
                var thumbnailStartTime = thumbnailEntry.Key;

                // Get the video thumbnail data and upload to photos folder
                var thumbnailHighResStream = await myVI.GetVideoThumbnailAsync(videoId, thumbnailId).ConfigureAwait(false);
                CloudBlockBlob thumbnailHighResBlob = containerVideoId.GetBlockBlobReference(dirHighRes + "/" + thumbnailId + ".jpg");
                await thumbnailHighResBlob.UploadFromStreamAsync(thumbnailHighResStream).ConfigureAwait(false);

                // let's create the low res version
                using (var thumbnailLowResStream = new MemoryStream())
                {
                    thumbnailHighResStream.Seek(0L, SeekOrigin.Begin);
                    var settings = new ResizeSettings { MaxWidth = 192 };
                    ImageBuilder.Current.Build(thumbnailHighResStream, thumbnailLowResStream, settings);
                    thumbnailLowResStream.Seek(0L, SeekOrigin.Begin);
                    CloudBlockBlob thumbnailLowRes = containerVideoId.GetBlockBlobReference(dirLowRes + "/" + thumbnailId + ".jpg");
                    await thumbnailLowRes.UploadFromStreamAsync(thumbnailLowResStream).ConfigureAwait(false);
                }

                // Submit the image to Azure's Computer Vision API
                var result = await vision.AnalyzeImageAsync(thumbnailHighResBlob.Uri.ToString(), features).ConfigureAwait(false);

                // cleaning metadata on blobs
                thumbnailHighResBlob.Metadata.Clear();
                thumbnailHighResBlob.Metadata.Add("Index", index.ToString(CultureInfo.InvariantCulture));

                // Record the image description and tags in blob metadata
                if (result.Description.Captions.Count > 0)
                {
                    thumbnailHighResBlob.Metadata.Add("Description", result.Description.Captions[0].Text);
                    thumbnailHighResBlob.Metadata.Add("Confidence", (result.Description.Captions[0].Confidence * 100).ToString("F1", CultureInfo.InvariantCulture));

                    if (!string.IsNullOrEmpty(translationLang))
                    {
                        try
                        {
                            string descriptionTranslated = await Translator.TranslateTextRequest(translatorSubscriptionKey, translatorEndpoint, result.Description.Captions[0].Text, translationLang).ConfigureAwait(false);
                            thumbnailHighResBlob.Metadata.Add("DescriptionTranslated", Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptionTranslated)));
                        }
                        catch (Exception exc)
                        {
                            ViewBag.ErrorMsg = exc.ToString();
                            Functions.SendProgress("Error in TranslateTextRequest:" + exc.Message, 0, 1);
                        }


                    }

                    //var guidThumbnail = Path.GetFileNameWithoutExtension(thumbnailHighResBlob.Name).Substring(18);
                }
                thumbnailHighResBlob.Metadata.Add("AdjustedStart", thumbnailStartTime.ToString());

                for (int i = 0; i < result.Description.Tags.Count; i++)
                {
                    string key = String.Format(CultureInfo.InvariantCulture, "Tag{0}", i);
                    thumbnailHighResBlob.Metadata.Add(key, result.Description.Tags[i]);
                }

                await thumbnailHighResBlob.SetMetadataAsync().ConfigureAwait(false);
            }

            // 100%
            Functions.SendProgress($"Processing {thumbnailsToProcessTimeAndId.Count} thumbnails...", 10, 10);
            //return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<ActionResult> Deleteall()
        {
            // Pass a list of blob URIs in ViewBag
            CloudStorageAccount account = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]);
            CloudBlobClient client = account.CreateCloudBlobClient();
            CloudBlobContainer container = client.GetContainerReference("photos");
            List<BlobInfo> blobs = new List<BlobInfo>();

            List<Task> myTasks = new List<Task>();
            foreach (IListBlobItem item in container.ListBlobs())
            {
                var blob = item as CloudBlockBlob;

                if (blob != null)
                {
                    myTasks.Add(blob.DeleteAsync());
                }
            }

            container = client.GetContainerReference("thumbnails");
            foreach (IListBlobItem item in container.ListBlobs())
            {
                var blob = item as CloudBlockBlob;

                if (blob != null)
                {
                    myTasks.Add(blob.DeleteAsync());
                }
            }

            await Task.WhenAll(myTasks.ToArray()).ConfigureAwait(false);

            return RedirectToAction("Index");
        }
    }
}