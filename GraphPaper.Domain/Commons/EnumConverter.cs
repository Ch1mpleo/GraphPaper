using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GraphPaper.Domain.Commons
{
    public static class ModelBuilderEnumExtensions
    {
        /// <summary>
        /// Converts all enum properties in the model to string columns.
        /// </summary>
        public static void UseStringForEnums(this ModelBuilder modelBuilder)
        {
            // Loop through every entity type in the model
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                // Loop through each property of that entity
                foreach (var property in entityType.GetProperties())
                {
                    var propertyType = property.ClrType;

                    // Handle nullable enums
                    var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

                    if (underlyingType.IsEnum)
                    {
                        var converterType = typeof(EnumToStringConverter<>).MakeGenericType(underlyingType);
                        var converter = (ValueConverter)Activator.CreateInstance(converterType, (ConverterMappingHints?)null)!;

                        property.SetValueConverter(converter);
                    }
                }
            }
        }
    }
}
