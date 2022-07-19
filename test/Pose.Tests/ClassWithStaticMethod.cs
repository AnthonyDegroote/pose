namespace Pose.Tests
{
    public class ClassWithStaticMethod
    {
        public bool ExposedMethod(string parameter)
        {
            return StaticMethod(parameter);
        }

        public static bool StaticMethod(string parameter)
        {
            return false;
        }

        public bool ExposedMethod()
        {
            return StaticMethod();
        }

        public static bool StaticMethod()
        {
            return false;
        }
    }
}
