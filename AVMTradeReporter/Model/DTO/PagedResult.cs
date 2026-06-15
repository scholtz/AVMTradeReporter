namespace AVMTradeReporter.Model.DTO
{
    public class PagedResult<T>
    {
        public IEnumerable<T> Items { get; set; } = [];
        public long Total { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; }
        public bool HasMore { get; set; }
    }
}
