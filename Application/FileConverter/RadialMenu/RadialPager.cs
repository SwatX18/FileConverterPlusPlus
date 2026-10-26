// <copyright file="RadialPager.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    /// <summary>
    /// Splits the items of the radial menu in pages of a fixed number of slices.
    /// </summary>
    public sealed class RadialPager<T>
    {
        public const int DefaultPageSize = 8;

        private readonly IReadOnlyList<T> items;
        private readonly int pageSize;

        public RadialPager(IReadOnlyList<T> items, int pageSize = DefaultPageSize)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), "The page size must be positive.");
            }

            this.items = items;
            this.pageSize = pageSize;
            this.PageCount = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
        }

        public int PageCount { get; }

        public int PageIndex { get; private set; }

        public IReadOnlyList<T> CurrentPage
        {
            get
            {
                return this.items.Skip(this.PageIndex * this.pageSize).Take(this.pageSize).ToList();
            }
        }

        public bool ShowsPaging => this.PageCount > 1;

        public string Indicator => string.Format(CultureInfo.InvariantCulture, "{0}/{1}", this.PageIndex + 1, this.PageCount);

        public void Next()
        {
            this.PageIndex = (this.PageIndex + 1) % this.PageCount;
        }

        public void Previous()
        {
            this.PageIndex = (this.PageIndex + this.PageCount - 1) % this.PageCount;
        }
    }
}
