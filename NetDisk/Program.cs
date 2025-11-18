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

using System;
using Aliyun.OSS.Common;
using Aliyun.OSS;


internal class Config
{
    public static string AccessKeyId = "LTAI5tKDhJYL9LPzURWz3Nbf";

    public static string AccessKeySecret = "7NRMO7As77WJGzeGmb5vgma7vCGe07";

    public static string Endpoint = "oss-cn-hangzhou.aliyuncs.com";
}

namespace NetDisk
{
    /// <summary>
    /// Sample for putting object.
    /// </summary>
    public static class FileSystem
    {
        static string accessKeyId = Config.AccessKeyId;
        static string accessKeySecret = Config.AccessKeySecret;
        static string endpoint = Config.Endpoint;
        static OssClient client = new OssClient(endpoint, accessKeyId, accessKeySecret);

        public static string PutObjectFromFile(string filePath)
        {
            string bucketName = "parful-netdisk";
            var key = Path.GetFileName(filePath);
            try
            {
                var _response = client.PutObject(bucketName, key, filePath);
                return client.GeneratePresignedUri(bucketName, key).ToString();
            }
            catch (OssException ex)
            {
                return string.Format("Failed with error code: {0}; Error info: {1}. \nRequestID:{2}\tHostID:{3}",ex.ErrorCode, ex.Message, ex.RequestId, ex.HostId);
            }
            catch (Exception ex)
            {
                return string.Format("Failed with error info: {0}", ex.Message);
            }
        }

    }
}


//static class Program
//{
//    static void Main()
//    {
//        var _fileUrl = CloudDisk.FileSystem.PutObjectFromFile("C:\\Users\\Administrator\\Pictures\\2.jpg");
//        Console.ReadKey();
//    }
//}