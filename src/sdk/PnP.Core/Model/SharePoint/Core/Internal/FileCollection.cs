using PnP.Core.QueryModel;
using PnP.Core.Services;
using System;
using System.IO;
// v============= HEU/LLM: Build an upload field projection. ==========
using System.Linq;
// ^===================================================================
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace PnP.Core.Model.SharePoint
{
    internal sealed class FileCollection : QueryableDataModelCollection<IFile>, IFileCollection
    {
        private const int maxFileSizeForRegularUpload = 10 * 1024 * 1024;

        #region Construction
        public FileCollection(PnPContext context, IDataModelParent parent, string memberName) : base(context, parent, memberName)
        {
            PnPContext = context;
            Parent = parent;
        }
        #endregion

        #region Add
        // v============= HEU/LLM: Keep uploads without a field projection unchanged. ==========
        // written by LLM, 2026-11-14
        public Task<IFile> AddAsync(string name, Stream content, bool overwrite = false)
            => AddAsync(name, content, overwrite, null);

        public async Task<IFile> AddAsync(string name, Stream content, bool overwrite, string[] listItemRestFieldNames)
        // ^===================================================================
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            var newFile = CreateNewAndAdd() as File;
            newFile.Name = name;

            if (content.Length <= maxFileSizeForRegularUpload)
            {
                // v============= HEU/LLM: Include selected item fields in the upload response. ==========
                newFile = await FileUpload(newFile, content, overwrite, listItemRestFieldNames).ConfigureAwait(false);
                // ^===================================================================
            }
            else
            {
                // v============= HEU/LLM: Include selected item fields after the final chunk. ==========
                newFile = await ChunkedFileUpload(newFile, content, overwrite, listItemRestFieldNames).ConfigureAwait(false);
                // ^===================================================================
            }

            return newFile;
        }

        public IFile Add(string name, Stream content, bool overwrite = false)
        {
            return AddAsync(name, content, overwrite).GetAwaiter().GetResult();
        }

        public async Task<IFile> AddTemplateFileAsync(string serverRelativePageName, TemplateFileType templateFileType)
        {
            if (string.IsNullOrEmpty(serverRelativePageName))
            {
                throw new ArgumentNullException(nameof(serverRelativePageName));
            }

            // Replace # with - as AddTemplateFile cannot handle an # in the name. This replacement is the same behavior than the SharePoint UI does
            var encodedServerRelativePageName = WebUtility.UrlEncode(serverRelativePageName.Replace("'", "''").Replace("#", "-")).Replace("+", "%20");
            var newFile = CreateNewAndAdd() as File;
            // v============= HEU/LLM: Put the file path in the query string to allow long page names. ==========
            // written by LLM, 2026-09-14
            // covered by SharePointPageWithLongFileNameKeepsTitleAsync
            string fileCreateRequest = $"_api/web/getFolderById('{{Parent.Id}}')/files/AddTemplateFile(urlOfFile=@file,templateFileType={(int)templateFileType})?@file='{encodedServerRelativePageName}'";
            // ^===================================================================
            var api = new ApiCall(fileCreateRequest, ApiType.SPORest);
            await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);
            return newFile;
        }

        public IFile AddTemplateFile(string serverRelativePageName, TemplateFileType templateFileType)
        {
            return AddTemplateFileAsync(serverRelativePageName, templateFileType).GetAwaiter().GetResult();
        }
        #endregion

        #region File upload
        // v============= HEU/LLM: Pass the requested fields to the upload. ==========
        private static async Task<File> FileUpload(File newFile, Stream content, bool overwrite, string[] listItemRestFieldNames)
        // ^===================================================================
        {
            var encodedServerFileName = WebUtility.UrlEncode(newFile.Name.Replace("'", "''").Replace("%20", " ")).Replace("+", "%20");
            string fileCreateRequest = $"_api/web/getFolderById('{{Parent.Id}}')/files/addusingpath(decodedUrl='{encodedServerFileName}',overwrite={overwrite.ToString().ToLowerInvariant()})";
            // v============= HEU/LLM: Select list item fields in the same POST response. ==========
            // written by LLM, 2026-11-14
            fileCreateRequest = AddListItemProjection(fileCreateRequest, listItemRestFieldNames);
            // ^===================================================================
            var api = new ApiCall(fileCreateRequest, ApiType.SPORest)
            {
                Interactive = true,
                BinaryBody = ToByteArray(content)
            };
            await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);
            return newFile;
        }

        // v============= HEU/LLM: Pass requested fields to the final chunk. ==========
        private static async Task<File> ChunkedFileUpload(File newFile, Stream content, bool overwrite, string[] listItemRestFieldNames)
        // ^===================================================================
        {
            // 10 MB chunks
            int chunkSizeBytes = 10 * 1024 * 1024;

            // Upload the file in chunks
            var firstChunk = true;
            var uploadId = Guid.NewGuid();
            var offset = 0L;

            var buffer = new byte[chunkSizeBytes];
            int bytesRead;
            while (true)
            {
                bytesRead = content.Read(buffer, 0, buffer.Length);
            
                // A chunk can be returned smaller than the bytes requested. See https://docs.microsoft.com/en-us/dotnet/api/system.io.stream.read?view=net-5.0&viewFallbackFrom=net-3.1#System_IO_Stream_Read_System_Byte___System_Int32_System_Int32_
                // The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many bytes are not currently available,
                // or zero (0) if the end of the stream has been reached.
                var chunk = new byte[bytesRead];
                Array.Copy(buffer, chunk, chunk.Length);

                if (firstChunk)
                {
                    // Add empty file
                    var encodedServerFileName = WebUtility.UrlEncode(newFile.Name.Replace("'", "''").Replace("%20", " ")).Replace("+", "%20");
                    string fileCreateRequest = $"_api/web/getFolderById('{{Parent.Id}}')/files/addusingpath(decodedUrl='{encodedServerFileName}',overwrite={overwrite.ToString().ToLowerInvariant()})";
                    var api = new ApiCall(fileCreateRequest, ApiType.SPORest)
                    {
                        Interactive = true
                    };
                    await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);

                    // Add first chunk
                    var endpointUrl = $"_api/web/getFileById('{{Id}}')/startupload(uploadId=guid'{uploadId}')";
                    api = new ApiCall(endpointUrl, ApiType.SPORest)
                    {
                        Interactive = true,
                        BinaryBody = chunk
                    };
                    await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);
                    firstChunk = false;
                }
                else if (content.Position == content.Length)
                {
                    // Finalize upload
                    var endpointUrl = $"_api/web/getFileById('{{Id}}')/finishupload(uploadId=guid'{uploadId}',fileOffset={offset})";
                    // v============= HEU/LLM: Return item fields with the final upload response. ==========
                    // written by LLM, 2026-11-14
                    endpointUrl = AddListItemProjection(endpointUrl, listItemRestFieldNames);
                    // ^===================================================================
                    var api = new ApiCall(endpointUrl, ApiType.SPORest)
                    {
                        Interactive = true,
                        BinaryBody = chunk
                    };
                    await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);

                    break;
                }
                else
                {
                    var endpointUrl = $"_api/web/getFileById('{{Id}}')/continueupload(uploadId=guid'{uploadId}',fileOffset={offset})";
                    var api = new ApiCall(endpointUrl, ApiType.SPORest)
                    {
                        Interactive = true,
                        BinaryBody = chunk
                    };
                    await newFile.RequestAsync(api, HttpMethod.Post).ConfigureAwait(false);
                }

                offset += bytesRead;
            }

            return newFile;
        }

        // v============= HEU/LLM: Add a list item projection without another read. ==========
        // written by LLM, 2026-11-14
        private static string AddListItemProjection(string request, string[] fieldNames)
        {
            if (fieldNames == null || fieldNames.Length == 0)
            {
                return request;
            }
            if (fieldNames.Any(name => string.IsNullOrWhiteSpace(name)
                || name.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            {
                throw new ArgumentException("The upload projection must use REST field names.", nameof(fieldNames));
            }
            var selected = new[] { "Id" }.Concat(fieldNames).Distinct(StringComparer.Ordinal)
                .Select(name => "ListItemAllFields/" + name);
            return request + "?$expand=ListItemAllFields&$select=*," + string.Join(",", selected);
        }
        // ^===================================================================

        private static byte[] ToByteArray(Stream source)
        {
            using (var memoryStream = new MemoryStream())
            {
                source.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
        #endregion
    }
}
