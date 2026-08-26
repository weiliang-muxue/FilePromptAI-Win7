using System;
using System.IO;

internal static class InstalledUserJourneySmokeTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: InstalledUserJourneySmokeTest.exe " +
                "<packaged-app-exe> <isolated-data-root>");
            return 2;
        }

        try
        {
            string evidence = PackagedUiJourney.Run(args[0], args[1]);
            Console.WriteLine(
                "PASS | installed user journey | " + evidence);
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(
                "FAIL | installed user journey | " + exception.Message);
            return 2;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(
                "FAIL | installed user journey | " + exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "FAIL | installed user journey | " + exception);
            return 1;
        }
    }
}
