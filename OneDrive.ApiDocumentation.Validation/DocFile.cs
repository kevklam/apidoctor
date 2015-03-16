﻿namespace OneDrive.ApiDocumentation.Validation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.IO;

    /// <summary>
    /// A documentation file that may contain one more resources or API methods
    /// </summary>
    public class DocFile
    {
        #region Instance Variables
        protected bool m_hasScanRun;
        protected string m_BasePath;
        protected List<MarkdownDeep.Block> m_CodeBlocks = new List<MarkdownDeep.Block>();
        protected List<ResourceDefinition> m_Resources = new List<ResourceDefinition>();
        protected List<MethodDefinition> m_Requests = new List<MethodDefinition>();
        protected List<ResourceDefinition> m_JsonExamples = new List<ResourceDefinition>();
        protected List<string> m_Bookmarks = new List<string>();

//        protected List<MarkdownDeep.LinkInfo> m_Links = new List<MarkdownDeep.LinkInfo>();
        #endregion

        #region Properties
        /// <summary>
        /// Friendly name of the file
        /// </summary>
        public string DisplayName { get; protected set; }

        /// <summary>
        /// Path to the file on disk
        /// </summary>
        public string FullPath { get; protected set; }

        /// <summary>
        /// HTML-rendered version of the markdown source (for displaying)
        /// </summary>
        public string HtmlContent { get; protected set; }

        /// <summary>
        /// Contains information on the headers and content blocks found in this document.
        /// </summary>
        public List<string> ContentOutline { get; set; }

        public ResourceDefinition[] Resources
        {
            get { return m_Resources.ToArray(); }
        }

        public MethodDefinition[] Requests
        {
            get { return m_Requests.ToArray(); }
        }

        public AuthScopeDefinition[] AuthScopes { get; private set; }

        public ErrorDefinition[] ErrorCodes { get; private set; }

        public string[] LinkDestinations
        {
            get
            {
                var query = from p in MarkdownLinks
                            select p.def.url;
                return query.ToArray();
            }
        }

        /// <summary>
        /// Raw Markdown parsed blocks
        /// </summary>
        protected MarkdownDeep.Block[] OriginalMarkdownBlocks { get; set; }

        protected List<MarkdownDeep.LinkInfo> MarkdownLinks {get;set;}

        public DocSet Parent { get; private set; }
        #endregion

        #region Constructor
        protected DocFile()
        {
            ContentOutline = new List<string>();
        }

        public DocFile(string basePath, string relativePath, DocSet parent)
        {
            m_BasePath = basePath;
            FullPath = Path.Combine(basePath, relativePath.Substring(1));
            DisplayName = relativePath;
            Parent = parent;
            ContentOutline = new List<string>();

            m_Bookmarks = new List<string>();
        }
        #endregion

        #region Markdown Parsing

        protected void TransformMarkdownIntoBlocksAndLinks(string inputMarkdown)
        {
            MarkdownDeep.Markdown md = new MarkdownDeep.Markdown();
            md.SafeMode = false;
            md.ExtraMode = true;

            HtmlContent = md.Transform(inputMarkdown);
            OriginalMarkdownBlocks = md.Blocks;
            MarkdownLinks = new List<MarkdownDeep.LinkInfo>(md.FoundLinks);
        }


        /// <summary>
        /// Read the contents of the file into blocks and generate any resource or method definitions from the contents
        /// </summary>
        public bool Scan(out ValidationError[] errors)
        {
            m_hasScanRun = true;
            List<ValidationError> detectedErrors = new List<ValidationError>();
            
            try
            {
                using (StreamReader reader = File.OpenText(this.FullPath))
                {
                    TransformMarkdownIntoBlocksAndLinks(reader.ReadToEnd());
                }
            }
            catch (IOException ioex)
            {
                detectedErrors.Add(new ValidationError(ValidationErrorCode.ErrorOpeningFile, DisplayName, "Error reading file contents: {0}", ioex.Message));
                errors = detectedErrors.ToArray();
                return false;
            }
            catch (Exception ex)
            {
                detectedErrors.Add(new ValidationError(ValidationErrorCode.ErrorReadingFile, DisplayName, "Error reading file contents: {0}", ex.Message));
                errors = detectedErrors.ToArray();
                return false;
            }

            return ParseMarkdownBlocks(out errors);
        }

        private static bool IsHeaderBlock(MarkdownDeep.Block block, int maxDepth = 2)
        {
            var blockType = block.BlockType;
            if (maxDepth >= 1 && blockType == MarkdownDeep.BlockType.h1) 
                return true;
            if (maxDepth >= 2 && blockType == MarkdownDeep.BlockType.h2)
                return true;
            if (maxDepth >= 3 && blockType == MarkdownDeep.BlockType.h3)
                return true;
            if (maxDepth >= 4 && blockType == MarkdownDeep.BlockType.h4)
                return true;
            if (maxDepth >= 5 && blockType == MarkdownDeep.BlockType.h5)
                return true;
            if (maxDepth >= 6 && blockType == MarkdownDeep.BlockType.h6)
                return true;
                    
            return false;
        }


        protected string PreviewOfBlockContent(MarkdownDeep.Block block)
        {
            if (block == null) return string.Empty;
            if (block.Content == null) return string.Empty;

            const int PreviewLength = 35;

            string contentPreview = block.Content.Length > PreviewLength ? block.Content.Substring(0, PreviewLength) : block.Content;
            contentPreview = contentPreview.Replace('\n', ' ').Replace('\r', ' ');
            return contentPreview;
        }

        protected bool ParseMarkdownBlocks(out ValidationError[] errors)
        {
            List<ValidationError> detectedErrors = new List<ValidationError>();

            string methodTitle = null;
            string methodDescription = null;

            MarkdownDeep.Block previousHeaderBlock = null;

            List<object> StuffFoundInThisDoc = new List<object>();

            for (int i = 0; i < OriginalMarkdownBlocks.Length; i++)
            {
                var previousBlock = (i > 0) ? OriginalMarkdownBlocks[i - 1] : null;
                var block = OriginalMarkdownBlocks[i];

                ContentOutline.Add(string.Format("{0} - {1}", block.BlockType, PreviewOfBlockContent(block)));

                // Capture GitHub Flavored Markdown Bookmarks
                if (IsHeaderBlock(block, 6))
                {
                    AddBookmarkForHeader(block.Content);
                }

                // Capture h1 and/or p element to be used as the title and description for items on this page
                if (IsHeaderBlock(block))
                {
                    methodTitle = block.Content;
                    methodDescription = null;       // Clear this because we don't want new title + old description
                    detectedErrors.Add(new ValidationMessage(null, "Found title: {0}", methodTitle));
                }
                else if (block.BlockType == MarkdownDeep.BlockType.p && IsHeaderBlock(previousBlock))
                {
                    methodDescription = block.Content;
                    detectedErrors.Add(new ValidationMessage(null, "Found description: {0}", methodDescription));
                }
                else if (block.BlockType == MarkdownDeep.BlockType.html)
                {
                    // If the next block is a codeblock we've found a metadata + codeblock pair
                    MarkdownDeep.Block nextBlock = null;
                    if (i + 1 < OriginalMarkdownBlocks.Length)
                    {
                        nextBlock = OriginalMarkdownBlocks[i + 1];
                    }
                    if (null != nextBlock && nextBlock.BlockType == MarkdownDeep.BlockType.codeblock)
                    {
                        // html + codeblock = likely request or response!
                        var definition = ParseCodeBlock(block, nextBlock);
                        if (null != definition)
                        {
                            detectedErrors.Add(new ValidationMessage(null, "Found code block: {0} [{1}]", definition.Title, definition.GetType().Name));
                            definition.Title = methodTitle;
                            definition.Description = methodDescription;

                            if (!StuffFoundInThisDoc.Contains(definition))
                            {
                                StuffFoundInThisDoc.Add(definition);
                            }
                        }
                    }
                }
                else if (block.BlockType == MarkdownDeep.BlockType.table_spec)
                {
                    MarkdownDeep.Block blockBeforeTable = (i - 1 >= 0) ? OriginalMarkdownBlocks[i - 1] : null;
                    if (null == blockBeforeTable) continue;

                    ValidationError[] parseErrors;
                    var table = TableSpecConverter.ParseTableSpec(block, previousHeaderBlock, out parseErrors);
                    if (null != parseErrors) detectedErrors.AddRange(parseErrors);

                    detectedErrors.Add(new ValidationMessage(null, "Found table: {0}. Rows:\r\n{1}", table.Type,
                        (from r in table.Rows select Newtonsoft.Json.JsonConvert.SerializeObject(r, Newtonsoft.Json.Formatting.Indented)).ComponentsJoinedByString(" ,\r\n")));

                    StuffFoundInThisDoc.Add(table);
                }

                if (block.IsHeaderBlock())
                {
                    previousHeaderBlock = block;
                }
            }

            ValidationError[] postProcessingErrors;
            PostProcessFoundElements(StuffFoundInThisDoc, out postProcessingErrors);
            detectedErrors.AddRange(postProcessingErrors);
            
            errors = detectedErrors.ToArray();
            return !detectedErrors.Any(x => x.IsError);
        }

        private void AddBookmarkForHeader(string headerText)
        {
 	        string bookmark = headerText.ToLowerInvariant().Replace(' ', '-');
            m_Bookmarks.Add(bookmark);
        }

        private void PostProcessFoundElements(List<object> elementsFoundInDocument, out ValidationError[] postProcessingErrors)
        {
            /*
            if FoundMethods == 1 then
              Attach all tables found in the document to the method.

            else if FoundMethods > 1 then
              Table.Type == ErrorCodes
                - Attach errors to all methods in the file
              Table.Type == PathParameters
                - Find request with matching parameters
              Table.Type == Query String Parameters
                - Request may not have matching parameters, because query string parameters may not be part of the request
              Table.Type == Header Parameters
                - Find request with matching parameters
              Table.Type == Body Parameters
                - Find request with matching parameters
             */

            List<ValidationError> detectedErrors = new List<ValidationError>();

            var foundMethods = from s in elementsFoundInDocument
                               where s is MethodDefinition
                               select (MethodDefinition)s;

            var foundResources = from s in elementsFoundInDocument
                                 where s is ResourceDefinition
                                 select (ResourceDefinition)s;

            var foundTables = from s in elementsFoundInDocument
                                   where s is TableDefinition
                                   select (TableDefinition)s;

            PostProcessAuthScopes(elementsFoundInDocument);
            PostProcessResources(foundResources, foundTables);
            PostProcessMethods(foundMethods, foundTables, detectedErrors);

            postProcessingErrors = detectedErrors.ToArray();
        }

        private void PostProcessAuthScopes(List<object> StuffFoundInThisDoc)
        {
            var authScopeTables = (from s in StuffFoundInThisDoc
                                   where s is TableDefinition && ((TableDefinition)s).Type == TableBlockType.AuthScopes
                                   select ((TableDefinition)s));

            List<AuthScopeDefinition> foundScopes = new List<AuthScopeDefinition>();
            foreach (var table in authScopeTables)
            {
                foundScopes.AddRange(table.Rows.Cast<AuthScopeDefinition>());
            }
            AuthScopes = foundScopes.ToArray();
        }

        private static void PostProcessResources(IEnumerable<ResourceDefinition> foundResources, IEnumerable<TableDefinition> foundTables)
        {
            if (foundResources.Count() == 1)
            {
                var onlyResource = foundResources.Single();
                foreach (var table in foundTables)
                {
                    switch (table.Type)
                    {
                        case TableBlockType.ResourcePropertyDescriptions:
                            onlyResource.Parameters.AddRange(table.Rows.Cast<ParameterDefinition>());
                            break;
                    }
                }
            }
        }

        private void PostProcessMethods(IEnumerable<MethodDefinition> foundMethods, IEnumerable<TableDefinition> foundTables, List<ValidationError> errors)
        {
            var totalMethods = foundMethods.Count();
            var totalTables = foundTables.Count();

            if (totalTables == 0)
                return;

            if (totalMethods == 0)
            {
                StoreOnFile(foundTables);
            }
            else if (totalMethods == 1)
            {
                var onlyMethod = foundMethods.Single();
                StoreOnMethod(foundTables, onlyMethod);
            }
            else
            {
                // TODO: Figure out how to map stuff when more than one method exists
                if (null != errors)
                {
                    errors.Add(new ValidationWarning(ValidationErrorCode.UnmappedDocumentElements, "Unable to map elements in file {0}", DisplayName));
                    
                    var unmappedMethods = (from m in foundMethods select m.RequestMetadata.MethodName).ComponentsJoinedByString("\r\n");
                    if (!string.IsNullOrEmpty(unmappedMethods)) 
                        errors.Add(new ValidationMessage("Unmapped methods", unmappedMethods));

                    var unmappedTables = (from t in foundTables select string.Format("{0} - {1}", t.Title, t.Type)).ComponentsJoinedByString("\r\n");
                    if (!string.IsNullOrEmpty(unmappedTables)) 
                        errors.Add(new ValidationMessage("Unmapped tables", unmappedTables));
                }
            }
        }

        private static void StoreOnMethod(IEnumerable<TableDefinition> foundTables, MethodDefinition onlyMethod)
        {
            foreach (var table in foundTables)
            {
                switch (table.Type)
                {
                    case TableBlockType.Unknown:
                        // Unknown table format, nothing we can do with it.
                        break;
                    case TableBlockType.EnumerationValues:
                        // TODO: Support enumeration values
                        Console.WriteLine("Enumeration that wasn't handled: {0} on method {1} ", table.Title, onlyMethod.RequestMetadata.MethodName);
                        break;
                    case TableBlockType.ErrorCodes:
                        onlyMethod.Errors = table.Rows.Cast<ErrorDefinition>().ToList();
                        break;
                    case TableBlockType.HttpHeaders:
                    case TableBlockType.PathParameters:
                    case TableBlockType.QueryStringParameters:
                        onlyMethod.Parameters.AddRange(table.Rows.Cast<ParameterDefinition>());
                        break;
                    case TableBlockType.RequestObjectProperties:
                        onlyMethod.RequestBodyParameters.AddRange(table.Rows.Cast<ParameterDefinition>());
                        break;
                    case TableBlockType.ResourcePropertyDescriptions:
                    case TableBlockType.ResponseObjectProperties:
                        Console.WriteLine("Object description that wasn't handled: {0} on method {1}", table.Title, onlyMethod.RequestMetadata.MethodName);
                        break;
                    default:
                        Console.WriteLine("Something else that wasn't handled: type:{0}, title:{1} on method {2}", table.Type, table.Title, onlyMethod.RequestMetadata.MethodName);
                        break;
                }
            }
        }

        private void StoreOnFile(IEnumerable<TableDefinition> foundTables)
        {
            // Assume anything we found is a global resource
            foreach (var table in foundTables)
            {
                switch (table.Type)
                {
                    case TableBlockType.ErrorCodes:
                        ErrorCodes = table.Rows.Cast<ErrorDefinition>().ToArray();
                        break;
                }
            }
        }

        ///// <summary>
        ///// Parse through the markdown blocks and intprerate the documents into
        ///// our internal object model.
        ///// </summary>
        ///// <returns><c>true</c>, if code blocks was parsed, <c>false</c> otherwise.</returns>
        ///// <param name="errors">Errors.</param>
        //protected bool ParseMarkdownBlocksOld(out ValidationError[] errors)
        //{
        //    List<ValidationError> detectedErrors = new List<ValidationError>();

        //    // Scan through the blocks to find something interesting
        //    m_CodeBlocks = FindCodeBlocks(OriginalMarkdownBlocks);

        //    for (int i = 0; i < m_CodeBlocks.Count; )
        //    {
        //        // We're looking for pairs of html + code blocks. The HTML block contains metadata about the block.
        //        // If we don't find an HTML block, then we skip the code block.
        //        var htmlComment = m_CodeBlocks[i];
        //        if (htmlComment.BlockType != MarkdownDeep.BlockType.html)
        //        {
        //            detectedErrors.Add(new ValidationMessage(FullPath, "Block skipped - expected HTML comment, found: {0}", htmlComment.BlockType, htmlComment.Content));
        //            i++;
        //            continue;
        //        }

        //        try
        //        {
        //            var codeBlock = m_CodeBlocks[i + 1];
        //            ParseCodeBlock(htmlComment, codeBlock);
        //        }
        //        catch (Exception ex)
        //        {
        //            detectedErrors.Add(new ValidationError(ValidationErrorCode.MarkdownParserError, FullPath, "Exception while parsing code blocks: {0}.", ex.Message));
        //        }
        //        i += 2;
        //    }

        //    errors = detectedErrors.ToArray();
        //    return detectedErrors.Count == 0;
        //}

        /// <summary>
        /// Filters the blocks to just a collection of blocks that may be
        /// relevent for our purposes
        /// </summary>
        /// <returns>The code blocks.</returns>
        /// <param name="blocks">Blocks.</param>
        protected static List<MarkdownDeep.Block> FindCodeBlocks(MarkdownDeep.Block[] blocks)
        {
            var blockList = new List<MarkdownDeep.Block>();
            foreach (var block in blocks)
            {
                switch (block.BlockType)
                {
                    case MarkdownDeep.BlockType.codeblock:
                    case MarkdownDeep.BlockType.html:
                        blockList.Add(block);
                        break;
                    default:
                        break;
                }
            }
            return blockList;
        }

        /// <summary>
        /// Convert an annotation and fenced code block in the documentation into something usable. Adds
        /// the detected object into one of the internal collections of resources, methods, or examples.
        /// </summary>
        /// <param name="metadata"></param>
        /// <param name="code"></param>
        public ItemDefinition ParseCodeBlock(MarkdownDeep.Block metadata, MarkdownDeep.Block code)
        {
            if (metadata.BlockType != MarkdownDeep.BlockType.html)
                throw new ArgumentException("metadata block does not appear to be metadata");

            if (code.BlockType != MarkdownDeep.BlockType.codeblock)
                throw new ArgumentException("code block does not appear to be code");

            var metadataJsonString = metadata.Content.Substring(4, metadata.Content.Length - 9);
            var annotation = CodeBlockAnnotation.FromJson(metadataJsonString);

            switch (annotation.BlockType)
            {
                case CodeBlockType.Resource:
                    {
                        var resource = new ResourceDefinition(annotation, code.Content, this);
                        m_Resources.Add(resource);
                        return resource;
                    }
                case CodeBlockType.Request:
                    {
                        var method = MethodDefinition.FromRequest(code.Content, annotation, this);
                        if (string.IsNullOrEmpty(method.Identifier))
                            method.Identifier = string.Format("{0} #{1}", DisplayName, m_Requests.Count);
                        m_Requests.Add(method);
                        return method;
                    }

                case CodeBlockType.Response:
                    {
                        var method = m_Requests.Last();
                        method.AddExpectedResponse(code.Content, annotation);
                        return method;
                    }
                case CodeBlockType.Example:
                    {
                        var example = new ExampleDefinition(annotation, code.Content, this);
                        m_JsonExamples.Add(example);
                        return example;
                    }
                case CodeBlockType.Ignored:
                    return null;
                default:
                    throw new NotSupportedException("Unsupported block type: " + annotation.BlockType);
            }
        }

        public MarkdownDeep.Block[] CodeBlocks
        {
            get { return m_CodeBlocks.ToArray(); }
        }
        #endregion

        #region Link Verification

        public bool ValidateNoBrokenLinks(bool includeWarnings, out ValidationError[] errors)
        {
            string[] files;
            return ValidateNoBrokenLinks(includeWarnings, out errors, out files);
        }

        /// <summary>
        /// Checks all links detected in the source document to make sure they are valid.
        /// </summary>
        /// <param name="errors">Information about broken links</param>
        /// <returns>True if all links are valid. Otherwise false</returns>
        public bool ValidateNoBrokenLinks(bool includeWarnings, out ValidationError[] errors, out string[] linkedDocFiles)
        {
            if (!m_hasScanRun)
                throw new InvalidOperationException("Cannot validate links until Scan() is called.");

            List<string> linkedPages = new List<string>();
            string relativeFileName = null;

            var foundErrors = new List<ValidationError>();
            foreach (var link in MarkdownLinks)
            {
                if (null == link.def)
                {
                    foundErrors.Add(new ValidationError(ValidationErrorCode.MissingLinkSourceId, this.DisplayName, "Link specifies ID '{0}' which was not found in the document.", link.link_text));
                    continue;
                }

                var result = VerifyLink(FullPath, link.def.url, m_BasePath, out relativeFileName);
                switch (result)
                {
                    case LinkValidationResult.BookmarkSkipped:
                    case LinkValidationResult.ExternalSkipped:
                        if (includeWarnings)
                            foundErrors.Add(new ValidationWarning(ValidationErrorCode.LinkValidationSkipped, this.DisplayName, "Skipped validation of link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.FileNotFound:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkDestinationNotFound, this.DisplayName, "Destination missing for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.ParentAboveDocSetPath:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkDestinationOutsideDocSet, this.DisplayName, "Destination outside of doc set for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.UrlFormatInvalid:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkFormatInvalid, this.DisplayName, "Invalid URL format for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.Valid:
                        foundErrors.Add(new ValidationMessage(this.DisplayName, "Link to URL '{0}' is valid.", link.def.url, link.link_text));
                        if (null != relativeFileName)
                        {
                            linkedPages.Add(relativeFileName);
                        }
                        break;
                    default:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.Unknown, this.DisplayName, "{2}: for link '{1}' to URL '{0}'", link.def.url, link.link_text, result));
                        break;

                }
                
            }
            errors = foundErrors.ToArray();
            linkedDocFiles = linkedPages.Distinct().ToArray();
            return !(errors.WereErrors() || errors.WereWarnings());
        }

        protected enum LinkValidationResult
        {
            Valid,
            FileNotFound,
            UrlFormatInvalid,
            ExternalSkipped,
            BookmarkSkipped,
            ParentAboveDocSetPath,
            BookmarkMissing,
            FileExistsBookmarkValidationSkipped,
            BookmarkSkippedDocFileNotFound
        }

        protected LinkValidationResult VerifyLink(string docFilePath, string linkUrl, string docSetBasePath, out string relativeFileName)
        {
            relativeFileName = null;
            Uri parsedUri;
            var validUrl = Uri.TryCreate(linkUrl, UriKind.RelativeOrAbsolute, out parsedUri);

            FileInfo sourceFile = new FileInfo(docFilePath);

            if (validUrl)
            {
                if (parsedUri.IsAbsoluteUri && (parsedUri.Scheme == "http" || parsedUri.Scheme == "https"))
                {
                    // TODO: verify an external URL is valid by making a HEAD request
                    return LinkValidationResult.ExternalSkipped;
                }
                else if (linkUrl.StartsWith("#"))
                {
                    string bookmarkName = linkUrl.Substring(1);
                    if (m_Bookmarks.Contains(bookmarkName))
                    {
                        return LinkValidationResult.Valid;
                    }
                    else 
                    {
                        return LinkValidationResult.BookmarkMissing;
                    }
                }
                else
                {
                    return VerifyRelativeLink(sourceFile, linkUrl, docSetBasePath, out relativeFileName);
                }
            }
            else
            {
                return LinkValidationResult.UrlFormatInvalid;
            }
        }

        protected virtual LinkValidationResult VerifyRelativeLink(FileInfo sourceFile, string linkUrl, string docSetBasePath, out string relativeFileName)
        {
            relativeFileName = null;
            var rootPath = sourceFile.DirectoryName;
            string bookmarkName = null;
            if (linkUrl.Contains("#"))
            {
                int indexOfHash = linkUrl.IndexOf('#');
                bookmarkName = linkUrl.Substring(indexOfHash + 1);
                linkUrl = linkUrl.Substring(0, indexOfHash);
            }

            while (linkUrl.StartsWith(".." + Path.DirectorySeparatorChar))
            {
                var nextLevelParent = new DirectoryInfo(rootPath).Parent;
                rootPath = nextLevelParent.FullName;
                linkUrl = linkUrl.Substring(3);
            }

            if (rootPath.Length < docSetBasePath.Length)
            {
                return LinkValidationResult.ParentAboveDocSetPath;
            }

            var pathToFile = Path.Combine(rootPath, linkUrl);
            FileInfo info = new FileInfo(pathToFile);
            if (!info.Exists)
            {
                return LinkValidationResult.FileNotFound;
            }

            relativeFileName = Parent.RelativePathToFile(info.FullName, urlStyle: true);

            if (bookmarkName != null)
            {
                // See if that bookmark exists in the target document, assuming we know about it
                var otherDocFile = Parent.LookupFileForPath(relativeFileName);
                if (otherDocFile == null)
                {
                    return LinkValidationResult.BookmarkSkippedDocFileNotFound;
                }
                else if (!otherDocFile.m_Bookmarks.Contains(bookmarkName))
                {
                    return LinkValidationResult.BookmarkMissing;
                }
            }

            return LinkValidationResult.Valid;
        }

        #endregion

    }

    public enum DocType
    {
        Unknown = 0,
        Resource,
        MethodRequest
    }


}
