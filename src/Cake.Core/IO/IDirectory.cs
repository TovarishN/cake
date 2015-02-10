﻿using System;
using System.Collections.Generic;

namespace Cake.Core.IO
{
    /// <summary>
    /// Represents a directory.
    /// </summary>
    public interface IDirectory : IFileSystemInfo
    {
        /// <summary>
        /// Gets the path to the directory.
        /// </summary>
        /// <value>The path.</value>
        new DirectoryPath Path { get; }

        /// <summary>
        /// Creates the directory.
        /// </summary>
        void Create();

        /// <summary>
        /// Deletes the directory.
        /// </summary>
        /// <param name="recursive">Will perform a recursive delete if set to <c>true</c>.</param>
        void Delete(bool recursive);

        /// <summary>
        /// Gets directories matching the specified filter and scope.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <param name="scope">The search scope.</param>
        /// <returns>Directories matching the filter and scope.</returns>
        IEnumerable<IDirectory> GetDirectories(string filter, SearchScope scope);

        /// <summary>
        /// Gets files matching the specified filter and scope.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <param name="scope">The search scope.</param>
        /// <returns>Files matching the specified filter and scope.</returns>
        IEnumerable<IFile> GetFiles(string filter, SearchScope scope);
    }
}