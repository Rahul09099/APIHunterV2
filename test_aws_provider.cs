using System;
using UnsecuredAPIKeys.Providers.Cloud_Providers;

namespace TestAWSProvider
{
    class Program
    {
        static void Main(string[] args)
        {
            // Test 1: Validate IsValidKeyFormat with valid Access Key ID
            var provider = new AWSIAMProvider();
            
            Console.WriteLine("Test 1: Valid Access Key ID format");
            var validKey = "AKIAIOSFODNN7EXAMPLE";
            var isValid = provider.GetType()
                .GetMethod("IsValidKeyFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(provider, new object[] { validKey });
            Console.WriteLine($"  Input: {validKey}");
            Console.WriteLine($"  Result: {isValid}");
            Console.WriteLine();
            
            // Test 2: Validate IsValidKeyFormat with invalid key
            Console.WriteLine("Test 2: Invalid Access Key ID format");
            var invalidKey = "INVALID_KEY";
            isValid = provider.GetType()
                .GetMethod("IsValidKeyFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(provider, new object[] { invalidKey });
            Console.WriteLine($"  Input: {invalidKey}");
            Console.WriteLine($"  Result: {isValid}");
            Console.WriteLine();
            
            // Test 3: Validate IsValidKeyFormat with delimited format
            Console.WriteLine("Test 3: Delimited format (Access Key + Secret)");
            var delimitedKey = "AKIAIOSFODNN7EXAMPLE:::wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
            isValid = provider.GetType()
                .GetMethod("IsValidKeyFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(provider, new object[] { delimitedKey });
            Console.WriteLine($"  Input: {delimitedKey}");
            Console.WriteLine($"  Result: {isValid}");
            Console.WriteLine();
            
            // Test 4: Test ExtractCredentialPair with delimited format
            Console.WriteLine("Test 4: Extract credential pair from delimited format");
            try
            {
                var extractMethod = provider.GetType()
                    .GetMethod("ExtractCredentialPair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var result = extractMethod?.Invoke(provider, new object[] { delimitedKey });
                if (result != null)
                {
                    var tuple = ((string, string))result;
                    Console.WriteLine($"  Access Key ID: {tuple.Item1}");
                    Console.WriteLine($"  Secret Key: {tuple.Item2}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.InnerException?.Message ?? ex.Message}");
            }
            Console.WriteLine();
            
            // Test 5: Test ExtractCredentialPair with standalone Access Key ID
            Console.WriteLine("Test 5: Extract credential pair from standalone Access Key ID");
            try
            {
                var extractMethod = provider.GetType()
                    .GetMethod("ExtractCredentialPair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var result = extractMethod?.Invoke(provider, new object[] { validKey });
                if (result != null)
                {
                    var tuple = ((string, string))result;
                    Console.WriteLine($"  Access Key ID: {tuple.Item1}");
                    Console.WriteLine($"  Secret Key: {(string.IsNullOrEmpty(tuple.Item2) ? "(empty - to be found in context)" : tuple.Item2)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.InnerException?.Message ?? ex.Message}");
            }
            Console.WriteLine();
            
            Console.WriteLine("All tests completed!");
        }
    }
}
