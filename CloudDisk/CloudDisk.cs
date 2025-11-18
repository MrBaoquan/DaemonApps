/*
 * Copyright (C) Alibaba Cloud Computing
 * All rights reserved.
 * 
 */

using System;
using System.IO;
using System.Threading;
using Aliyun.OSS.Common;
using System.Text;
using Aliyun.OSS.Util;
using System.Security.Cryptography;

using Aliyun.OSS;

using IniParser;
using IniParser.Model;
using System.Collections.Generic;
using System.Linq;

namespace CloudDisk
{

    public class ConfigMgr
    {
        private static ConfigMgr _instance;
        public static ConfigMgr Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = new ConfigMgr();
                return _instance;
            }
        }

        public FileIniDataParser parser = new FileIniDataParser();
        public IniData data;
        public string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloud-disk.ini");

        internal string AccessKeyId => data["Authroization"]["accessKeyId"];
        internal string AccessKeySecret => data["Authroization"]["accessKeySecret"];
        public string Endpoint => data["Authroization"]["endpoint"];
        public string BucketName => data["Authroization"]["bucketName"];

        public string Domain => data["Settings"]["domain"];
        public bool UseSSL => bool.Parse(data["Settings"]["useSSL"]);
        public bool IsCName => bool.Parse(data["Settings"]["isCName"]);

        public ConfigMgr()
        {
            if (!File.Exists(ConfigPath))
            {
                data = new IniData();
                parser.WriteFile(ConfigPath, data);
            }

            data = parser.ReadFile(ConfigPath);
            data["Authroization"]["accessKeyId"] = data["Authroization"]["accessKeyId"]?? "LTAI5tNeJvByceRseJHj3huE";
            data["Authroization"]["accessKeySecret"] = data["Authroization"]["accessKeySecret"]?? "B2ZupSGlyu9SmwHpoDLvTXSazE7QqH";
            data["Authroization"]["endpoint"] = data["Authroization"]["endpoint"]?? "oss-cn-beijing.aliyuncs.com";
            data["Authroization"]["bucketName"] = data["Authroization"]["bucketName"]?? "and-disk";

            data["Settings"]["domain"] = data["Settings"]["domain"] ?? "disk.andcrane.com";
            data["Settings"]["useSSL"] = data["Settings"]["useSSL"] ?? "false";
            data["Settings"]["isCName"] = data["Settings"]["isCName"] ?? "true";

            parser.WriteFile(ConfigPath, data);
        }

        public string GetOSSObjectUrl(string key)
        {
            var _protocol = UseSSL? "https" : "http";
            var _domain = IsCName ? Domain : Endpoint;
            return string.Format("{0}://{1}/{2}", _protocol, _domain, key);
        }
    }

    /// <summary>
    /// Sample for putting object.
    /// </summary>
    public static class FileSystem
    {
        private static ConfigMgr Config = ConfigMgr.Instance;
        private static string accessKeyId => Config.AccessKeyId;
        private static string accessKeySecret = Config.AccessKeySecret;
        private static string endpoint = Config.Endpoint;
        private static OssClient client = new OssClient(endpoint, accessKeyId, accessKeySecret);

        // 上传文件到OSS
        public static bool PutObjectFromFile(string filePath, out string imgUrl, string folder="")
        {
            var key = Path.Combine(folder,Path.GetFileName(filePath)).Replace("\\", "/");
            try
            {
                var _response = client.PutObject(Config.BucketName, key, filePath);
                imgUrl = Config.GetOSSObjectUrl(key);
                return true;
            }
            catch (OssException ex)
            {
                imgUrl = string.Format("Failed with error code: {0}; Error info: {1}. \nRequestID:{2}\tHostID:{3}", ex.ErrorCode, ex.Message, ex.RequestId, ex.HostId);
                return false;
            }
            catch (Exception ex)
            {
                imgUrl = string.Format("Failed with error info: {0}", ex.Message);
                return false;
            }
        }

        // 下载文件到本地
        public static void DownloadObjectToFile(string key, string localFilePath)
        {
            var client = new OssClient(endpoint, accessKeyId, accessKeySecret);
            try
            {
                var getObjectRequest = new GetObjectRequest(Config.BucketName, key);
                getObjectRequest.StreamTransferProgress += (_1, args) =>
                {
                    var _progress = args.TransferredBytes*100/args.TotalBytes;


                };
                // 下载文件。
                var ossObject = client.GetObject(getObjectRequest);
                using (var stream = ossObject.Content)
                {
                    var buffer = new byte[1024 * 1024];
                    var bytesRead = 0;
                    var fs = File.Open(localFilePath, FileMode.OpenOrCreate);
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, bytesRead);
                    }
                    fs.Close();
                }
                Console.WriteLine("Get object:{0} succeeded", key);
            }
            catch (OssException ex)
            {
                Console.WriteLine("Failed with error code: {0}; Error info: {1}. \nRequestID:{2}\tHostID:{3}",
                    ex.ErrorCode, ex.Message, ex.RequestId, ex.HostId);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed with error info: {0}", ex.Message);
            }
        }


        

        // 列举文件
        public static IEnumerable<OssObjectSummary>  ListObjects(string prefix = "", string marker = "", int maxKeys = 100)
        {
            try
            {
                var keys = new List<string>();
                ObjectListing result = null;
                string nextMarker = string.Empty;
                
                var listObjectsRequest = new ListObjectsRequest(Config.BucketName)
                {
                    Marker = nextMarker,
                    MaxKeys = 100,
                    Prefix = prefix,
                };
                result = client.ListObjects(listObjectsRequest);
                nextMarker = result.NextMarker;
                return result.ObjectSummaries;
            }
            catch (OssException ex)
            {
                Console.WriteLine("Failed with error code: {0}; Error info: {1}. \nRequestID:{2}\tHostID:{3}",
                    ex.ErrorCode, ex.Message, ex.RequestId, ex.HostId);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed with error info: {0}", ex.Message);
            }
            return new List<OssObjectSummary>();
        }
    }
}