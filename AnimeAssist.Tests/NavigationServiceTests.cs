using AniMeido.App.Services;

namespace AniMeido.Tests
{
    public class NavigationServiceTests
    {
        [Fact]
        public void GoBack_EmptyStack_ReturnsFalse()
        {
            var stack = new NavigationStack();
            Assert.False(stack.CanGoBack);
        }

        [Fact]
        public void PushAndPop_PreservesPage()
        {
            var stack = new NavigationStack();
            var page = new object();
            stack.Push(page, "hello");
            Assert.True(stack.CanGoBack);
            var entry = stack.Pop();
            Assert.Same(page, entry.Page);
        }

        [Fact]
        public void PushAndPop_PreservesParameter()
        {
            var stack = new NavigationStack();
            var page = new object();
            stack.Push(page, 42);
            var entry = stack.Pop();
            Assert.Equal(42, entry.Parameter);
        }

        [Fact]
        public void PushAndPop_ParameterCanBeNull()
        {
            var stack = new NavigationStack();
            var page = new object();
            stack.Push(page, null);
            var entry = stack.Pop();
            Assert.Null(entry.Parameter);
        }

        [Fact]
        public void Clear_ResetsStack()
        {
            var stack = new NavigationStack();
            stack.Push(new object(), "a");
            stack.Push(new object(), 1);
            stack.Clear();
            Assert.False(stack.CanGoBack);
        }

        [Fact]
        public void MultiplePushPop_OrderIsCorrect()
        {
            var stack = new NavigationStack();
            var page1 = new object();
            var page2 = new object();
            var page3 = new object();
            stack.Push(page1, "first");
            stack.Push(page2, 2);
            stack.Push(page3, 3.0);

            var e1 = stack.Pop();
            Assert.Same(page3, e1.Page);
            Assert.Equal(3.0, e1.Parameter);

            var e2 = stack.Pop();
            Assert.Same(page2, e2.Page);
            Assert.Equal(2, e2.Parameter);

            var e3 = stack.Pop();
            Assert.Same(page1, e3.Page);
            Assert.Equal("first", e3.Parameter);

            Assert.False(stack.CanGoBack);
        }

        [Fact]
        public void Pop_EmptyStack_Throws()
        {
            var stack = new NavigationStack();
            Assert.Throws<InvalidOperationException>(() => stack.Pop());
        }

        [Fact]
        public void Push_DuplicateTopPage_DoesNotDuplicate()
        {
            var stack = new NavigationStack();
            var page = new object();
            stack.Push(page, "a");
            stack.Push(page, "a"); // same page reference
            stack.Pop(); // remove one
            Assert.False(stack.CanGoBack); // should only have one entry
        }
    }
}
