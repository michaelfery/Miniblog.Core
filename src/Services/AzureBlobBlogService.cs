﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Miniblog.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.IO;
using System.Xml.XPath;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Miniblog.Core.Services
{
    public class AzureBlobBlogService : IBlogService
    {
        private const string FILES = "files";

        private readonly List<Post> _cache = new List<Post>();
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly CloudBlobContainer _container;

        public AzureBlobBlogService(IConfiguration config, IHttpContextAccessor contextAccessor)
        {
            _contextAccessor = contextAccessor;

            // Retrieve Azure storage account from connection string
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config.GetConnectionString("AzureStorageAccount"));

            // Create a CloudBlobClient object for credentialed access to Azure Blobs
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            // Get a reference to the container
            _container = blobClient.GetContainerReference(config["AzureStorage:PostsContainerName"]);
            _container.CreateIfNotExistsAsync().Wait();
            var permissions = new BlobContainerPermissions() { PublicAccess = BlobContainerPublicAccessType.Blob };
            _container.SetPermissionsAsync(permissions).ConfigureAwait(false);
        
            Initialize();
        }

        public virtual Task<IEnumerable<Post>> GetPosts(int count, int skip = 0)
        {
            bool isAdmin = IsAdmin();

            var utcNow = DateTime.UtcNow;
            var posts = _cache
                .Where(p => p.PubDate <= DateTime.UtcNow && (p.IsPublished || isAdmin))
                .Skip(skip)
                .Take(count);

            return Task.FromResult(posts);
        }

        public virtual Task<IEnumerable<Post>> GetPostsByCategory(string category)
        {
            bool isAdmin = IsAdmin();

            var posts = from p in _cache
                        where p.PubDate <= DateTime.UtcNow && (p.IsPublished || isAdmin)
                        where p.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)
                        select p;

            return Task.FromResult(posts);
        }

        public virtual Task<Post> GetPostBySlug(string slug)
        {
            var post = _cache.FirstOrDefault(p => p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
            bool isAdmin = IsAdmin();

            if (post != null && post.PubDate <= DateTime.UtcNow && (post.IsPublished || isAdmin))
            {
                return Task.FromResult(post);
            }

            return Task.FromResult<Post>(null);
        }

        public virtual Task<Post> GetPostById(string id)
        {
            var post = _cache.FirstOrDefault(p => p.ID.Equals(id, StringComparison.OrdinalIgnoreCase));
            bool isAdmin = IsAdmin();

            if (post != null && post.PubDate <= DateTime.UtcNow && (post.IsPublished || isAdmin))
            {
                return Task.FromResult(post);
            }

            return Task.FromResult<Post>(null);
        }

        public virtual Task<IEnumerable<string>> GetCategories()
        {
            bool isAdmin = IsAdmin();

            var categories = _cache
                .Where(p => p.IsPublished || isAdmin)
                .SelectMany(post => post.Categories)
                .Select(cat => cat.ToLowerInvariant())
                .Distinct();

            return Task.FromResult(categories);
        }

        public async Task SavePost(Post post)
        {
            string filePath = GetFilePath(post);
            post.LastModified = DateTime.UtcNow;

            XDocument doc = new XDocument(
                            new XElement("post",
                                new XElement("title", post.Title),
                                new XElement("slug", post.Slug),
                                new XElement("pubDate", FormatDateTime(post.PubDate)),
                                new XElement("lastModified", FormatDateTime(post.LastModified)),
                                new XElement("excerpt", post.Excerpt),
                                new XElement("content", post.Content),
                                new XElement("ispublished", post.IsPublished),
                                new XElement("categories", string.Empty),
                                new XElement("comments", string.Empty)
                            ));

            XElement categories = doc.XPathSelectElement("post/categories");
            foreach (string category in post.Categories)
            {
                categories.Add(new XElement("category", category));
            }

            XElement comments = doc.XPathSelectElement("post/comments");
            foreach (Comment comment in post.Comments)
            {
                comments.Add(
                    new XElement("comment",
                        new XElement("author", comment.Author),
                        new XElement("email", comment.Email),
                        new XElement("date", FormatDateTime(comment.PubDate)),
                        new XElement("content", comment.Content),
                        new XAttribute("isAdmin", comment.IsAdmin),
                        new XAttribute("id", comment.ID)
                    ));
            }

            var fileRef = _container.GetBlockBlobReference(filePath);

            using (MemoryStream ms = new MemoryStream())
            {
                doc.Save(ms);
                // Rewind the stream ready to read from it elsewhere
                ms.Position = 0;
                await fileRef.UploadFromStreamAsync(ms);
            }

            if (!_cache.Contains(post))
            {
                _cache.Add(post);
                SortCache();
            }
        }

        public Task DeletePost(Post post)
        {
            string filePath = GetFilePath(post);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            if (_cache.Contains(post))
            {
                _cache.Remove(post);
            }

            return Task.CompletedTask;
        }

        public async Task<string> SaveFile(byte[] bytes, string fileName, string suffix = null)
        {
            suffix = CleanFromInvalidChars(suffix ?? DateTime.UtcNow.Ticks.ToString());

            string ext = Path.GetExtension(fileName);
            string name = CleanFromInvalidChars(Path.GetFileNameWithoutExtension(fileName));

            string fileNameWithSuffix = $"{name}_{suffix}{ext}";

            string absolute = Path.Combine(FILES, fileNameWithSuffix);
            string dir = Path.GetDirectoryName(absolute);

            var fileRef = _container.GetBlockBlobReference(absolute);
            await fileRef.UploadFromByteArrayAsync(bytes, 0, bytes.Length);
            
            return fileRef.Uri.ToString();
        }

        private string GetFilePath(Post post)
        {
            return Path.Combine(post.ID + ".xml");
        }

        private void Initialize()
        {
            LoadPostsAsync().Wait();
            SortCache();
        }

        private async Task LoadPostsAsync()
        {
            BlobContinuationToken continuationToken = null;
            do
            {
                var response = await _container.ListBlobsSegmentedAsync(continuationToken);
                continuationToken = response.ContinuationToken;
                
                // Can this be done in parallel to speed it up?
                foreach (var item in response.Results.OfType<CloudBlockBlob>())
                {
                    CloudBlockBlob blob = (CloudBlockBlob)item;
                    XElement doc;
                    using (var stream = new MemoryStream())
                    {
                        await blob.DownloadToStreamAsync(stream);
                        stream.Position = 0;
                        doc = XElement.Load(stream);
                    }

                    Post post = new Post
                    {
                        ID = Path.GetFileNameWithoutExtension(blob.Name),
                        Title = ReadValue(doc, "title"),
                        Excerpt = ReadValue(doc, "excerpt"),
                        Content = ReadValue(doc, "content"),
                        Slug = ReadValue(doc, "slug").ToLowerInvariant(),
                        PubDate = DateTime.Parse(ReadValue(doc, "pubDate")),
                        LastModified = DateTime.Parse(ReadValue(doc, "lastModified", DateTime.UtcNow.ToString(CultureInfo.InvariantCulture))),
                        IsPublished = bool.Parse(ReadValue(doc, "ispublished", "true")),
                    };

                    LoadCategories(post, doc);
                    LoadComments(post, doc);
                    _cache.Add(post);
                }
            }
            while (continuationToken != null);
        }

        private static void LoadCategories(Post post, XElement doc)
        {
            XElement categories = doc.Element("categories");
            if (categories == null)
                return;

            List<string> list = new List<string>();

            foreach (var node in categories.Elements("category"))
            {
                list.Add(node.Value);
            }

            post.Categories = list.ToArray();
        }

        private static void LoadComments(Post post, XElement doc)
        {
            var comments = doc.Element("comments");

            if (comments == null)
                return;

            foreach (var node in comments.Elements("comment"))
            {
                Comment comment = new Comment()
                {
                    ID = ReadAttribute(node, "id"),
                    Author = ReadValue(node, "author"),
                    Email = ReadValue(node, "email"),
                    IsAdmin = bool.Parse(ReadAttribute(node, "isAdmin", "false")),
                    Content = ReadValue(node, "content"),
                    PubDate = DateTime.Parse(ReadValue(node, "date", "2000-01-01")),
                };

                post.Comments.Add(comment);
            }
        }

        private static string ReadValue(XElement doc, XName name, string defaultValue = "")
        {
            if (doc.Element(name) != null)
                return doc.Element(name)?.Value;

            return defaultValue;
        }

        private static string ReadAttribute(XElement element, XName name, string defaultValue = "")
        {
            if (element.Attribute(name) != null)
                return element.Attribute(name)?.Value;

            return defaultValue;
        }

        private static string CleanFromInvalidChars(string input)
        {
            // ToDo: what we are doing here if we switch the blog from windows
            // to unix system or vice versa? we should remove all invalid chars for both systems

            var regexSearch = Regex.Escape(new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars()));
            var r = new Regex($"[{regexSearch}]");
            return r.Replace(input, "");
        }
        
        private static string FormatDateTime(DateTime dateTime)
        {
            const string UTC = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'";

            return dateTime.Kind == DateTimeKind.Utc
                ? dateTime.ToString(UTC)
                : dateTime.ToUniversalTime().ToString(UTC);
        }

        protected void SortCache()
        {
            _cache.Sort((p1, p2) => p2.PubDate.CompareTo(p1.PubDate));
        }

        protected bool IsAdmin()
        {
            return _contextAccessor.HttpContext?.User?.Identity.IsAuthenticated == true;
        }
    }
}