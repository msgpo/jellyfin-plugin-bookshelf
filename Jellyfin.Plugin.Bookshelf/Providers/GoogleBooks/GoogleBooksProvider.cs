﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookshelf.Providers.GoogleBooks
{
    public class GoogleBooksProvider : IRemoteMetadataProvider<Book, BookInfo>
    {
        // first pattern matches "book (2000)" and gives the name and the year
        // last resort matches the whole string as the name
        private static readonly Regex[] NameMatches = new[] {
            new Regex(@"(?<name>.*)\((?<year>\d{4}\))"),
            new Regex(@"(?<name>.*)")
        };
        
        private static IHttpClient _httpClient;
        private static IJsonSerializer _jsonSerializer;
        private static ILogger _logger;

        public GoogleBooksProvider(ILogger logger, IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _logger = logger;
        }

        public string Name => "Google Books";

        public bool Supports(BaseItem item)
        {
            return item is Book;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<RemoteSearchResult>();

            return list;
        }

        public async Task<MetadataResult<Book>> GetMetadata(BookInfo item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetadataResult<Book> metadataResult = new MetadataResult<Book>();
            metadataResult.HasMetadata = false;

            var googleBookId = item.GetProviderId("GoogleBooks") ??
                await FetchBookId(item, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(googleBookId))
                return metadataResult;

            var bookResult = await FetchBookData(googleBookId, cancellationToken);

            if (bookResult == null)
                return metadataResult;

            metadataResult.Item = ProcessBookData(bookResult, cancellationToken);
            metadataResult.QueriedById = true;
            metadataResult.HasMetadata = true;
            return metadataResult;
        }

        private async Task<string> FetchBookId(BookInfo item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = item.Name;
            var year = string.Empty;

            foreach (var re in NameMatches)
            {
                Match m = re.Match(name);
                if (m.Success)
                {
                    name = m.Groups["name"].Value.Trim();
                    year = m.Groups["year"] != null ? m.Groups["year"].Value : null;
                    break;
                }
            }

            if (string.IsNullOrEmpty(year) && item.Year != null)
            {
                year = item.Year.ToString();
            }


            var url = string.Format(GoogleApiUrls.SearchUrl, WebUtility.UrlEncode(name), 0, 20);

            var stream = await _httpClient.SendAsync(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = true,
                EnableDefaultUserAgent = true
            }, "GET");

            if (stream == null)
            {
                _logger.LogInformation("response is null");
                return null;
            }

            var searchResults = _jsonSerializer.DeserializeFromStream<SearchResult>(stream.Content);

            if (searchResults == null || searchResults.items == null)
                return null;

            var comparableName = GetComparableName(item.Name);

            foreach (var i in searchResults.items)
            {
                // no match so move on to the next item
                if (!GetComparableName(i.volumeInfo.title).Equals(comparableName)) continue;

                if (!string.IsNullOrEmpty(year))
                {
                    // adjust for google yyyy-mm-dd format
                    var resultYear = i.volumeInfo.publishedDate.Length > 4 ? i.volumeInfo.publishedDate.Substring(0,4) : i.volumeInfo.publishedDate;

                    int bookReleaseYear;
                    if (Int32.TryParse(resultYear, out bookReleaseYear))
                    {
                        int localReleaseYear;
                        if (Int32.TryParse(year, out localReleaseYear))
                        {
                            // allow a one year variance
                            if (Math.Abs(bookReleaseYear - localReleaseYear) > 1)
                            {
                                continue;
                            }
                        }
                    }
                }

                // We have our match
                return i.id;
            }

            return null;
        }

        private async Task<BookResult> FetchBookData(string googleBookId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(GoogleApiUrls.DetailsUrl, googleBookId);

            var stream = await _httpClient.SendAsync(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = true,
                EnableDefaultUserAgent = true
            }, "GET");

            if (stream == null)
            {
                _logger.LogInformation("response is null");
                return null;
            }

            return _jsonSerializer.DeserializeFromStream<BookResult>(stream.Content);
        }

        private Book ProcessBookData(BookResult bookResult, CancellationToken cancellationToken)
        {
            var book = new Book();
            cancellationToken.ThrowIfCancellationRequested();

            book.Name = bookResult.volumeInfo.title;
            book.Overview = bookResult.volumeInfo.description;
            try
            {
                book.ProductionYear = bookResult.volumeInfo.publishedDate.Length > 4
                    ? Convert.ToInt32(bookResult.volumeInfo.publishedDate.Substring(0, 4))
                    : Convert.ToInt32(bookResult.volumeInfo.publishedDate);
            }
            catch (Exception)
            {
                _logger.LogError("Error parsing date");
            }

            if (!string.IsNullOrEmpty(bookResult.volumeInfo.publisher))
                book.Studios.Append(bookResult.volumeInfo.publisher);

            if (!string.IsNullOrEmpty(bookResult.volumeInfo.mainCatagory))
                book.Tags.Append(bookResult.volumeInfo.mainCatagory);
            
            if (bookResult.volumeInfo.catagories != null && bookResult.volumeInfo.catagories.Count > 0)
            {
                foreach (var category in bookResult.volumeInfo.catagories)
                    book.Tags.Append(category);
            }

            // google rates out of five so convert to ten
            book.CommunityRating = bookResult.volumeInfo.averageRating * 2;

            if (!string.IsNullOrEmpty(bookResult.id))
                book.SetProviderId("GoogleBooks", bookResult.id);

            return book;
        }

        private const string Remove = "\"'!`?";
        // convert these characters to whitespace for better matching
        // there are two dashes with different char codes
        private const string Spacers = "/,.:;\\(){}[]+-_=–*";

        internal static string GetComparableName(string name)
        {
            name = name.ToLower();
            name = name.Normalize(NormalizationForm.FormKD);

            foreach (var pair in ReplaceEndNumerals)
            {
                if (name.EndsWith(pair.Key))
                {
                    name = name.Remove(name.IndexOf(pair.Key, StringComparison.InvariantCulture), pair.Key.Length);
                    name += pair.Value;
                }
            }

            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (c >= 0x2B0 && c <= 0x0333)
                {
                    // skip char modifier and diacritics 
                }
                else if (Remove.IndexOf(c) > -1)
                {
                    // skip chars we are removing
                }
                else if (Spacers.IndexOf(c) > -1)
                {
                    sb.Append(" ");
                }
                else if (c == '&')
                {
                    sb.Append(" and ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            name = sb.ToString();
            name = name.Replace("the", " ");
            name = name.Replace(" - ", ": ");

            string prevName;
            do
            {
                prevName = name;
                name = name.Replace("  ", " ");
            } while (name.Length != prevName.Length);

            return name.Trim();
        }

        static readonly Dictionary<string, string> ReplaceEndNumerals = new Dictionary<string, string> {
            {" i", " 1"},
            {" ii", " 2"},
            {" iii", " 3"},
            {" iv", " 4"},
            {" v", " 5"},
            {" vi", " 6"},
            {" vii", " 7"},
            {" viii", " 8"},
            {" ix", " 9"},
            {" x", " 10"}
        };

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url
            });
        }
    }
}
