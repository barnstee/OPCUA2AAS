
using Opc.Ua.Configuration;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Opc.Ua.Sample.Simulation
{
    public class ApplicationMessageDlg : IApplicationMessageDlg
    {
        private string message = string.Empty;
        private bool ask = false;

        public override void Message(string text, bool ask)
        {
            this.message = text;
            this.ask = ask;
        }

        public override async Task<bool> ShowAsync()
        {
            if (ask)
            {
                message += " (y/n, default y): ";
                Console.Write(message);
            }
            else
            {
                Console.WriteLine(message);
            }
            if (ask)
            {
                try
                {
                    ConsoleKeyInfo result = Console.ReadKey();
                    Console.WriteLine();
                    return await Task.FromResult((result.KeyChar == 'y') || (result.KeyChar == 'Y') || (result.KeyChar == '\r'));
                }
                catch
                {
                    // intentionally fall through
                }
            }
            return await Task.FromResult(true);
        }
    }

    public class Program
    {
        public static string defaultNodeset = "Station.NodeSet2.xml";
        public static void Main(string[] args)
        {
            try
            {
                ConsoleServer(args).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: {0}", ex.Message);
            }
        }

        private static async Task ConsoleServer(string[] args)
        {
            ApplicationInstance.MessageDlg = new ApplicationMessageDlg();
            ApplicationInstance application = new ApplicationInstance();

            // load the application configuration.
            application.ConfigSectionName = "Opc.Ua.Station";
            application.ApplicationType = ApplicationType.Server;
            ApplicationConfiguration config = await application.LoadApplicationConfiguration(false).ConfigureAwait(false);

            // check the application certificate.
            await application.CheckApplicationInstanceCertificate(false, 0).ConfigureAwait(false);
            
            // figure out which nodeset file to use
            if (args.Length == 0)
            {
                // no arguments specified, use default
                Console.WriteLine("Using default Nodeset file: " + defaultNodeset);
                config.ApplicationUri = Path.Combine(Environment.CurrentDirectory, defaultNodeset); //Some place to stick an argument, there's probably a better way to do this
            }
            else
            {
                if (File.Exists(args[0]))   // relative path in argument
                {
                    Console.WriteLine("Using Nodeset file: " + args[0]);
                    config.ApplicationUri = args[0]; //Some place to stick an argument, there's probably a better way to do this
                }
                else  // absolute path in argumment
                {   
                    if (File.Exists(Path.Combine(Environment.CurrentDirectory, args[0])))
                    {
                        Console.WriteLine("Using Nodeset file: " + Path.Combine(Environment.CurrentDirectory, args[0]));
                        config.ApplicationUri = Path.Combine(Environment.CurrentDirectory, args[0]); //Some place to stick an argument, there's probably a better way to do this
                    } else
                    {
                        Console.WriteLine("Provided NodeSet argument not found in the file system.");
                        Console.WriteLine("Ensure the path exists and try again!");
                    }
                }
            }

            // create cert validator
            config.CertificateValidator = new CertificateValidator();
            config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);

            // start the server.
            await application.Start(new FactoryStationServer()).ConfigureAwait(false);

            Console.WriteLine("Server started.");
            Thread.Sleep(Timeout.Infinite);
        }

        private static void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                // accept all OPC UA client certificates
                Console.WriteLine("Automatically trusting client certificate " + e.Certificate.Subject);
                e.Accept = true;
            }
        }
    }
}
