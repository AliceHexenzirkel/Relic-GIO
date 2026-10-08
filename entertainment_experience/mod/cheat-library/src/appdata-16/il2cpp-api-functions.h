// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// Relic: game 1.6 il2cpp API RVAs from the PE export table of UserAssembly.dll (tools/matcher16.py; 174 exported, 48 not)
// Target Unity version: 2019.4.21 - 2019.4.24

#ifndef DO_API_NO_RETURN

#define DO_API_NO_RETURN(o, r, n, p) DO_API(o,r,n,p)

#endif



DO_API(0x05FA97C0, int, il2cpp_init, (const char* domain_name));  // 1.6: export table

DO_API(0x05FA9810, int, il2cpp_init_utf16, (const Il2CppChar * domain_name));  // 1.6: export table

DO_API(0x05FA9C40, void, il2cpp_shutdown, ());  // 1.6: export table

DO_API(0x05FA9BB0, void, il2cpp_set_config_dir, (const char *config_path));  // 1.6: export table

DO_API(0x05FA9BD0, void, il2cpp_set_data_dir, (const char *data_path));  // 1.6: export table

DO_API(0x05FA9C30, void, il2cpp_set_temp_dir, (const char *temp_path));  // 1.6: export table

DO_API(0x05FA9B80, void, il2cpp_set_commandline_arguments, (int argc, const char* const argv[], const char* basedir));  // 1.6: export table

DO_API(0x05FA9B90, void, il2cpp_set_commandline_arguments_utf16, (int argc, const Il2CppChar * const argv[], const char* basedir));  // 1.6: export table

DO_API(0x05FA9BC0, void, il2cpp_set_config_utf16, (const Il2CppChar * executablePath));  // 1.6: export table

DO_API(0x05FA9BA0, void, il2cpp_set_config, (const char* executablePath));  // 1.6: export table



DO_API(0x05FA9C00, void, il2cpp_set_memory_callbacks, (Il2CppMemoryCallbacks * callbacks));  // 1.6: export table

DO_API(0x05FA9750, const Il2CppImage*, il2cpp_get_corlib, ());  // 1.6: export table

DO_API(0x05FA90A0, void, il2cpp_add_internal_call, (const char* name, Il2CppMethodPointer method));  // 1.6: export table

DO_API(0x05FA9AF0, Il2CppMethodPointer, il2cpp_resolve_icall, (const char* name));  // 1.6: export table



DO_API(0x05FA90B0, void*, il2cpp_alloc, (size_t size));  // 1.6: export table

DO_API(0x05FA96B0, void, il2cpp_free, (void* ptr));  // 1.6: export table



// array

DO_API(0x05FA90C0, Il2CppClass*, il2cpp_array_class_get, (Il2CppClass * element_class, uint32_t rank));  // 1.6: export table

DO_API(0x05FA90F0, uint32_t, il2cpp_array_length, (Il2CppArray * array));  // 1.6: export table

DO_API(0x05FA90E0, uint32_t, il2cpp_array_get_byte_length, (Il2CppArray * array));  // 1.6: export table

DO_API(0x05FA9100, Il2CppArray*, il2cpp_array_new, (Il2CppClass * elementTypeInfo, il2cpp_array_size_t length));  // 1.6: export table

DO_API(0x05FA9120, Il2CppArray*, il2cpp_array_new_specific, (Il2CppClass * arrayTypeInfo, il2cpp_array_size_t length));  // 1.6: export table

DO_API(0x05FA9110, Il2CppArray*, il2cpp_array_new_full, (Il2CppClass * array_class, il2cpp_array_size_t * lengths, il2cpp_array_size_t * lower_bounds));  // 1.6: export table

DO_API(0x05FA9140, Il2CppClass*, il2cpp_bounded_array_class_get, (Il2CppClass * element_class, uint32_t rank, bool bounded));  // 1.6: export table

DO_API(0x05FA90D0, int, il2cpp_array_element_size, (const Il2CppClass * array_class));  // 1.6: export table



// assembly

DO_API(0x05FA9130, const Il2CppImage*, il2cpp_assembly_get_image, (const Il2CppAssembly * assembly));  // 1.6: export table



// class

DO_API(0x0, void, il2cpp_class_for_each, (void(*klassReportFunc)(Il2CppClass* klass, void* userData), void* userData));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x05FA9170, const Il2CppType*, il2cpp_class_enum_basetype, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9380, bool, il2cpp_class_is_generic, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9390, bool, il2cpp_class_is_inflated, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9350, bool, il2cpp_class_is_assignable_from, (Il2CppClass * klass, Il2CppClass * oklass));  // 1.6: export table

DO_API(0x05FA93B0, bool, il2cpp_class_is_subclass_of, (Il2CppClass * klass, Il2CppClass * klassc, bool check_interfaces));  // 1.6: export table

DO_API(0x05FA9310, bool, il2cpp_class_has_parent, (Il2CppClass * klass, Il2CppClass * klassc));  // 1.6: export table

DO_API(0x05FA9180, Il2CppClass*, il2cpp_class_from_il2cpp_type, (const Il2CppType * type));  // 1.6: export table

DO_API(0x05FA9190, Il2CppClass*, il2cpp_class_from_name, (const Il2CppImage * image, const char* namespaze, const char *name));  // 1.6: export table

DO_API(0x05FA91A0, Il2CppClass*, il2cpp_class_from_system_type, (Il2CppReflectionType * type));  // 1.6: export table

DO_API(0x05FA9200, Il2CppClass*, il2cpp_class_get_element_class, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9210, const EventInfo*, il2cpp_class_get_events, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA9230, FieldInfo*, il2cpp_class_get_fields, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA92B0, Il2CppClass*, il2cpp_class_get_nested_types, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA9260, Il2CppClass*, il2cpp_class_get_interfaces, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA92D0, const PropertyInfo*, il2cpp_class_get_properties, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA92E0, const PropertyInfo*, il2cpp_class_get_property_from_name, (Il2CppClass * klass, const char *name));  // 1.6: export table

DO_API(0x05FA9220, FieldInfo*, il2cpp_class_get_field_from_name, (Il2CppClass * klass, const char *name));  // 1.6: export table

DO_API(0x05FA9280, const MethodInfo*, il2cpp_class_get_methods, (Il2CppClass * klass, void* *iter));  // 1.6: export table

DO_API(0x05FA9270, const MethodInfo*, il2cpp_class_get_method_from_name, (Il2CppClass * klass, const char* name, int argsCount));  // 1.6: export table

DO_API(0x05FA9290, const char*, il2cpp_class_get_name, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x0, void, il2cpp_type_get_name_chunked, (const Il2CppType * type, void(*chunkReportFunc)(void* data, void* userData), void* userData));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x05FA92A0, const char*, il2cpp_class_get_namespace, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA92C0, Il2CppClass*, il2cpp_class_get_parent, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA91F0, Il2CppClass*, il2cpp_class_get_declaring_type, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9330, int32_t, il2cpp_class_instance_size, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA93D0, size_t, il2cpp_class_num_fields, (const Il2CppClass * enumKlass));  // 1.6: export table

DO_API(0x05FA93C0, bool, il2cpp_class_is_valuetype, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA93E0, int32_t, il2cpp_class_value_size, (Il2CppClass * klass, uint32_t * align));  // 1.6: export table

DO_API(0x05FA9360, bool, il2cpp_class_is_blittable, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9240, int, il2cpp_class_get_flags, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9340, bool, il2cpp_class_is_abstract, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA93A0, bool, il2cpp_class_is_interface, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9160, int, il2cpp_class_array_element_size, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9180, Il2CppClass*, il2cpp_class_from_type, (const Il2CppType * type));  // 1.6: export table

DO_API(0x05FA92F0, const Il2CppType*, il2cpp_class_get_type, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x0, uint32_t, il2cpp_class_get_type_token, (Il2CppClass * klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x05FA9300, bool, il2cpp_class_has_attribute, (Il2CppClass * klass, Il2CppClass * attr_class));  // 1.6: export table

DO_API(0x05FA9320, bool, il2cpp_class_has_references, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9370, bool, il2cpp_class_is_enum, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9250, const Il2CppImage*, il2cpp_class_get_image, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA91B0, const char*, il2cpp_class_get_assemblyname, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x0, int, il2cpp_class_get_rank, (const Il2CppClass * klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_class_get_data_size, (const Il2CppClass * klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void*, il2cpp_class_get_static_field_data, (const Il2CppClass * klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// testing only

DO_API(0x05FA91E0, size_t, il2cpp_class_get_bitmap_size, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA91C0, void, il2cpp_class_get_bitmap, (Il2CppClass * klass, size_t * bitmap));  // 1.6: export table



// stats

DO_API(0x05FA9C50, bool, il2cpp_stats_dump_to_file, (const char *path));  // 1.6: export table

DO_API(0x05FA9ED0, uint64_t, il2cpp_stats_get_value, (Il2CppStat stat));  // 1.6: export table



// domain

DO_API(0x05FA9450, Il2CppDomain*, il2cpp_domain_get, ());  // 1.6: export table

DO_API(0x05FA9440, const Il2CppAssembly*, il2cpp_domain_assembly_open, (Il2CppDomain * domain, const char* name));  // 1.6: export table

DO_API(0x05FA9460, const Il2CppAssembly**, il2cpp_domain_get_assemblies, (const Il2CppDomain * domain, size_t * size));  // 1.6: export table



// exception

DO_API_NO_RETURN(0x05FA9AC0, void, il2cpp_raise_exception, (Il2CppException*));  // 1.6: export table

DO_API(0x05FA94A0, Il2CppException*, il2cpp_exception_from_name_msg, (const Il2CppImage * image, const char *name_space, const char *name, const char *msg));  // 1.6: export table

DO_API(0x05FA9760, Il2CppException*, il2cpp_get_exception_argument_null, (const char *arg));  // 1.6: export table

DO_API(0x05FA9550, void, il2cpp_format_exception, (const Il2CppException * ex, char* message, int message_size));  // 1.6: export table

DO_API(0x05FA9600, void, il2cpp_format_stack_trace, (const Il2CppException * ex, char* output, int output_size));  // 1.6: export table

DO_API(0x05FAA130, void, il2cpp_unhandled_exception, (Il2CppException*));  // 1.6: export table



// field

DO_API(0x05FA94B0, int, il2cpp_field_get_flags, (FieldInfo * field));  // 1.6: export table

DO_API(0x05FA9250, const char*, il2cpp_field_get_name, (FieldInfo * field));  // 1.6: export table

DO_API(0x05FA9290, Il2CppClass*, il2cpp_field_get_parent, (FieldInfo * field));  // 1.6: export table

DO_API(0x05FA94C0, size_t, il2cpp_field_get_offset, (FieldInfo * field));  // 1.6: export table

DO_API(0x05FA94D0, const Il2CppType*, il2cpp_field_get_type, (FieldInfo * field));  // 1.6: export table

DO_API(0x05FA94E0, void, il2cpp_field_get_value, (Il2CppObject * obj, FieldInfo * field, void *value));  // 1.6: export table

DO_API(0x05FA94F0, Il2CppObject*, il2cpp_field_get_value_object, (FieldInfo * field, Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA9500, bool, il2cpp_field_has_attribute, (FieldInfo * field, Il2CppClass * attr_class));  // 1.6: export table

DO_API(0x05FA9510, void, il2cpp_field_set_value, (Il2CppObject * obj, FieldInfo * field, void *value));  // 1.6: export table

DO_API(0x05FA9530, void, il2cpp_field_static_get_value, (FieldInfo * field, void *value));  // 1.6: export table

DO_API(0x05FA9540, void, il2cpp_field_static_set_value, (FieldInfo * field, void *value));  // 1.6: export table

DO_API(0x05FA9520, void, il2cpp_field_set_value_object, (Il2CppObject * instance, FieldInfo * field, Il2CppObject * value));  // 1.6: export table

DO_API(0x0, bool, il2cpp_field_is_literal, (FieldInfo * field));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

// gc

DO_API(0x05FF2C80, void, il2cpp_gc_collect, (int maxGenerations));  // 1.6: export table

DO_API(0x05FA96D0, int32_t, il2cpp_gc_collect_a_little, ());  // 1.6: export table

DO_API(0x05FA96E0, void, il2cpp_gc_disable, ());  // 1.6: export table

DO_API(0x05FA96F0, void, il2cpp_gc_enable, ());  // 1.6: export table

DO_API(0x0, bool, il2cpp_gc_is_disabled, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, int64_t, il2cpp_gc_get_max_time_slice_ns, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_gc_set_max_time_slice_ns, (int64_t maxTimeSlice));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_gc_is_incremental, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x05FA9710, int64_t, il2cpp_gc_get_used_size, ());  // 1.6: export table

DO_API(0x05FA9700, int64_t, il2cpp_gc_get_heap_size, ());  // 1.6: export table

DO_API(0x0, void, il2cpp_gc_wbarrier_set_field, (Il2CppObject * obj, void **targetAddress, void *object));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_gc_has_strict_wbarriers, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_gc_set_external_allocation_tracker, (void(*func)(void*, size_t, int)));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_gc_set_external_wbarrier_tracker, (void(*func)(void**)));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_gc_foreach_heap, (void(*func)(void* data, void* userData), void* userData));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_stop_gc_world, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_start_gc_world, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

// gchandle

DO_API(0x05FA9730, uint32_t, il2cpp_gchandle_new, (Il2CppObject * obj, bool pinned));  // 1.6: export table

DO_API(0x05FA9740, uint32_t, il2cpp_gchandle_new_weakref, (Il2CppObject * obj, bool track_resurrection));  // 1.6: export table

DO_API(0x05FA9720, Il2CppObject*, il2cpp_gchandle_get_target, (uint32_t gchandle));  // 1.6: export table

DO_API(0x05FE4240, void, il2cpp_gchandle_free, (uint32_t gchandle));  // 1.6: export table

DO_API(0x0, void , il2cpp_gchandle_foreach_get_target, (void(*func)(void* data, void* userData), void* userData));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// vm runtime info

DO_API(0x0, uint32_t, il2cpp_object_header_size, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_array_object_header_size, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_offset_of_array_length_in_array_object_header, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_offset_of_array_bounds_in_array_object_header, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_allocation_granularity, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// liveness

DO_API(0x05FAA140, void*, il2cpp_unity_liveness_calculation_begin, (Il2CppClass * filter, int max_object_count, il2cpp_register_object_callback callback, void* userdata, il2cpp_WorldChangedCallback onWorldStarted, il2cpp_WorldChangedCallback onWorldStopped));  // 1.6: export table

DO_API(0x05FAA150, void, il2cpp_unity_liveness_calculation_end, (void* state));  // 1.6: export table

DO_API(0x05FAA160, void, il2cpp_unity_liveness_calculation_from_root, (Il2CppObject * root, void* state));  // 1.6: export table

DO_API(0x05FAA170, void, il2cpp_unity_liveness_calculation_from_statics, (void* state));  // 1.6: export table



// method

DO_API(0x05FA92F0, const Il2CppType*, il2cpp_method_get_return_type, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA92A0, Il2CppClass*, il2cpp_method_get_declaring_type, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9290, const char*, il2cpp_method_get_name, (const MethodInfo * method));  // 1.6: export table

DO_API(0x0, const MethodInfo*, il2cpp_method_get_from_reflection, (const Il2CppReflectionMethod * method));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x05FA9900, Il2CppReflectionMethod*, il2cpp_method_get_object, (const MethodInfo * method, Il2CppClass * refclass));  // 1.6: export table

DO_API(0x05FA9960, bool, il2cpp_method_is_generic, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9970, bool, il2cpp_method_is_inflated, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9980, bool, il2cpp_method_is_instance, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9920, uint32_t, il2cpp_method_get_param_count, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9910, const Il2CppType*, il2cpp_method_get_param, (const MethodInfo * method, uint32_t index));  // 1.6: export table

DO_API(0x05FA92A0, Il2CppClass*, il2cpp_method_get_class, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9950, bool, il2cpp_method_has_attribute, (const MethodInfo * method, Il2CppClass * attr_class));  // 1.6: export table

DO_API(0x05FA98D0, uint32_t, il2cpp_method_get_flags, (const MethodInfo * method, uint32_t * iflags));  // 1.6: export table

DO_API(0x05FA9940, uint32_t, il2cpp_method_get_token, (const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9930, const char*, il2cpp_method_get_param_name, (const MethodInfo * method, uint32_t index));  // 1.6: export table



// profiler

#if IL2CPP_ENABLE_PROFILER





#endif



// property

DO_API(0x05FA9AB0, uint32_t, il2cpp_property_get_flags, (PropertyInfo * prop));  // 1.6: export table

DO_API(0x05FA9290, const MethodInfo*, il2cpp_property_get_get_method, (PropertyInfo * prop));  // 1.6: export table

DO_API(0x05FA92A0, const MethodInfo*, il2cpp_property_get_set_method, (PropertyInfo * prop));  // 1.6: export table

DO_API(0x05FA94D0, const char*, il2cpp_property_get_name, (PropertyInfo * prop));  // 1.6: export table

DO_API(0x05FA9250, Il2CppClass*, il2cpp_property_get_parent, (PropertyInfo * prop));  // 1.6: export table



// object

DO_API(0x05FA9250, Il2CppClass*, il2cpp_object_get_class, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA9A00, uint32_t, il2cpp_object_get_size, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA9A10, const MethodInfo*, il2cpp_object_get_virtual_method, (Il2CppObject * obj, const MethodInfo * method));  // 1.6: export table

DO_API(0x05FA9A20, Il2CppObject*, il2cpp_object_new, (const Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9A40, void*, il2cpp_object_unbox, (Il2CppObject * obj));  // 1.6: export table



DO_API(0x05FAA180, Il2CppObject*, il2cpp_value_box, (Il2CppClass * klass, void* data));  // 1.6: export table



// monitor

DO_API(0x05FA9990, void, il2cpp_monitor_enter, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA99D0, bool, il2cpp_monitor_try_enter, (Il2CppObject * obj, uint32_t timeout));  // 1.6: export table

DO_API(0x05FA99A0, void, il2cpp_monitor_exit, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA99B0, void, il2cpp_monitor_pulse, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA99C0, void, il2cpp_monitor_pulse_all, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA99F0, void, il2cpp_monitor_wait, (Il2CppObject * obj));  // 1.6: export table

DO_API(0x05FA99E0, bool, il2cpp_monitor_try_wait, (Il2CppObject * obj, uint32_t timeout));  // 1.6: export table



// runtime

DO_API(0x05FA9B10, Il2CppObject*, il2cpp_runtime_invoke, (const MethodInfo * method, void *obj, void **params, Il2CppException **exc));  // 1.6: export table

DO_API(0x05FA9B30, Il2CppObject*, il2cpp_runtime_invoke_convert_args, (const MethodInfo * method, void *obj, Il2CppObject **params, int paramCount, Il2CppException **exc));  // 1.6: export table

DO_API(0x05FA9B00, void, il2cpp_runtime_class_init, (Il2CppClass * klass));  // 1.6: export table

DO_API(0x05FA9B50, void, il2cpp_runtime_object_init, (Il2CppObject * obj));  // 1.6: export table



DO_API(0x05FA9B60, void, il2cpp_runtime_object_init_exception, (Il2CppObject * obj, Il2CppException** exc));  // 1.6: export table



DO_API(0x05FA9B70, void, il2cpp_runtime_unhandled_exception_policy_set, (Il2CppRuntimeUnhandledExceptionPolicy value));  // 1.6: export table



// string

DO_API(0x05FA9F80, int32_t, il2cpp_string_length, (Il2CppString * str));  // 1.6: export table

DO_API(0x05FA9F50, Il2CppChar*, il2cpp_string_chars, (Il2CppString * str));  // 1.6: export table

DO_API(0x05FA9FB0, Il2CppString*, il2cpp_string_new, (const char* str));  // 1.6: export table

DO_API(0x05FA9F90, Il2CppString*, il2cpp_string_new_len, (const char* str, uint32_t length));  // 1.6: export table

DO_API(0x05FA9FA0, Il2CppString*, il2cpp_string_new_utf16, (const Il2CppChar * text, int32_t len));  // 1.6: export table

DO_API(0x05FA9FB0, Il2CppString*, il2cpp_string_new_wrapper, (const char* str));  // 1.6: export table

DO_API(0x05FA9F60, Il2CppString*, il2cpp_string_intern, (Il2CppString * str));  // 1.6: export table

DO_API(0x05FA9F70, Il2CppString*, il2cpp_string_is_interned, (Il2CppString * str));  // 1.6: export table



// thread

DO_API(0x05FC1440, Il2CppThread*, il2cpp_thread_current, ());  // 1.6: export table

DO_API(0x05FA9FC0, Il2CppThread*, il2cpp_thread_attach, (Il2CppDomain * domain));  // 1.6: export table

DO_API(0x05FA9FD0, void, il2cpp_thread_detach, (Il2CppThread * thread));  // 1.6: export table



DO_API(0x05FA9FE0, Il2CppThread**, il2cpp_thread_get_all_attached_threads, (size_t * size));  // 1.6: export table

DO_API(0x05FA98C0, bool, il2cpp_is_vm_thread, (Il2CppThread * thread));  // 1.6: export table



// stacktrace

DO_API(0x05FA9430, void, il2cpp_current_thread_walk_frame_stack, (Il2CppFrameWalkFunc func, void* user_data));  // 1.6: export table

DO_API(0x05FAA030, void, il2cpp_thread_walk_frame_stack, (Il2CppThread * thread, Il2CppFrameWalkFunc func, void* user_data));  // 1.6: export table

DO_API(0x05FA9420, bool, il2cpp_current_thread_get_top_frame, (Il2CppStackFrameInfo * frame));  // 1.6: export table

DO_API(0x05FAA020, bool, il2cpp_thread_get_top_frame, (Il2CppThread * thread, Il2CppStackFrameInfo * frame));  // 1.6: export table

DO_API(0x05FA93F0, bool, il2cpp_current_thread_get_frame_at, (int32_t offset, Il2CppStackFrameInfo * frame));  // 1.6: export table

DO_API(0x05FA9FF0, bool, il2cpp_thread_get_frame_at, (Il2CppThread * thread, int32_t offset, Il2CppStackFrameInfo * frame));  // 1.6: export table

DO_API(0x05FA9400, int32_t, il2cpp_current_thread_get_stack_depth, ());  // 1.6: export table

DO_API(0x05FAA010, int32_t, il2cpp_thread_get_stack_depth, (Il2CppThread * thread));  // 1.6: export table

DO_API(0x0, void, il2cpp_override_stack_backtrace, (Il2CppBacktraceFunc stackBacktraceFunc));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// type

DO_API(0x05FAA110, Il2CppObject*, il2cpp_type_get_object, (const Il2CppType * type));  // 1.6: export table

DO_API(0x05FAA120, int, il2cpp_type_get_type, (const Il2CppType * type));  // 1.6: export table

DO_API(0x05FAA040, Il2CppClass*, il2cpp_type_get_class_or_element_class, (const Il2CppType * type));  // 1.6: export table

DO_API(0x05FAA050, char*, il2cpp_type_get_name, (const Il2CppType * type));  // 1.6: export table

DO_API(0x0, bool, il2cpp_type_is_byref, (const Il2CppType * type));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, uint32_t, il2cpp_type_get_attrs, (const Il2CppType * type));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_type_equals, (const Il2CppType * type, const Il2CppType * otherType));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, char*, il2cpp_type_get_assembly_qualified_name, (const Il2CppType * type));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_type_is_static, (const Il2CppType * type));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_type_is_pointer_type, (const Il2CppType * type));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// image

DO_API(0x05FA97A0, const Il2CppAssembly*, il2cpp_image_get_assembly, (const Il2CppImage * image));  // 1.6: export table

DO_API(0x05FA9250, const char*, il2cpp_image_get_name, (const Il2CppImage * image));  // 1.6: export table

DO_API(0x05FA9250, const char*, il2cpp_image_get_filename, (const Il2CppImage * image));  // 1.6: export table

DO_API(0x05FA97B0, const MethodInfo*, il2cpp_image_get_entry_point, (const Il2CppImage * image));  // 1.6: export table



DO_API(0x0, size_t, il2cpp_image_get_class_count, (const Il2CppImage * image));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, const Il2CppClass*, il2cpp_image_get_class, (const Il2CppImage * image, size_t index));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// Memory information

DO_API(0x05FA9150, Il2CppManagedMemorySnapshot*, il2cpp_capture_memory_snapshot, ());  // 1.6: export table

DO_API(0x05FA96C0, void, il2cpp_free_captured_memory_snapshot, (Il2CppManagedMemorySnapshot * snapshot));  // 1.6: export table



DO_API(0x05FA9BF0, void, il2cpp_set_find_plugin_callback, (Il2CppSetFindPlugInCallback method));  // 1.6: export table



// Logging

DO_API(0x05FA9AE0, void, il2cpp_register_log_callback, (Il2CppLogCallback method));  // 1.6: export table



// Debugger

DO_API(0x0, void, il2cpp_debugger_set_agent_options, (const char* options));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_is_debugger_attached, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, void, il2cpp_register_debugger_agent_transport, (Il2CppDebuggerTransport * debuggerTransport));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// Debug metadata

DO_API(0x0, bool, il2cpp_debug_get_method_info, (const MethodInfo*, Il2CppMethodDebugInfo * methodDebugInfo));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// TLS module

DO_API(0x0, void, il2cpp_unity_install_unitytls_interface, (const void* unitytlsInterfaceStruct));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// custom attributes

DO_API(0x0, Il2CppCustomAttrInfo*, il2cpp_custom_attrs_from_class, (Il2CppClass * klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, Il2CppCustomAttrInfo*, il2cpp_custom_attrs_from_method, (const MethodInfo * method));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



DO_API(0x0, Il2CppObject*, il2cpp_custom_attrs_get_attr, (Il2CppCustomAttrInfo * ainfo, Il2CppClass * attr_klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, bool, il2cpp_custom_attrs_has_attr, (Il2CppCustomAttrInfo * ainfo, Il2CppClass * attr_klass));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, Il2CppArray*, il2cpp_custom_attrs_construct, (Il2CppCustomAttrInfo * cinfo));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



DO_API(0x0, void, il2cpp_custom_attrs_free, (Il2CppCustomAttrInfo * ainfo));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



// Il2CppClass user data for GetComponent optimization

DO_API(0x0, void, il2cpp_class_set_userdata, (Il2CppClass * klass, void* userdata));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll

DO_API(0x0, int, il2cpp_class_get_userdata_offset, ());  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll



DO_API(0x0, void, il2cpp_set_default_thread_affinity, (int64_t affinity_mask));  // RELIC-TODO-16 not exported by the 1.6 UserAssembly.dll


